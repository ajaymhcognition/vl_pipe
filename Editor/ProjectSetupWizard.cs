// Packages/com.mhcockpit.vlpipe/Editor/ProjectSetupWizard.cs
// Menu: Tools → Virtual Lab → Project Setup

#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

#if ADDRESSABLES_INSTALLED
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
#endif

namespace MHCockpit.VLPipe.Editor
{
    // =========================================================================
    //  BOOTSTRAPPER — auto-writes ADDRESSABLES_INSTALLED scripting define
    // =========================================================================

    [InitializeOnLoad]
    internal static class AddressablesDefineBootstrapper
    {
        private const string DEFINE = "ADDRESSABLES_INSTALLED";
        private const string EDITOR_ASM = "Unity.Addressables.Editor";

        static AddressablesDefineBootstrapper()
        {
            bool present = AppDomain.CurrentDomain
                .GetAssemblies()
                .Any(a => a.GetName().Name.Equals(EDITOR_ASM, StringComparison.OrdinalIgnoreCase));

            if (present) AddDefine(DEFINE);
            else RemoveDefine(DEFINE);
        }

        private static void AddDefine(string d)
        {
#if UNITY_6000_0_OR_NEWER
            var t = NamedBuildTarget.FromBuildTargetGroup(
                        BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            string cur = PlayerSettings.GetScriptingDefineSymbols(t);
            if (cur.Split(';').Any(x => x.Trim() == d)) return;
            PlayerSettings.SetScriptingDefineSymbols(t,
                string.IsNullOrEmpty(cur) ? d : cur + ";" + d);
#else
            var grp = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            string cur = PlayerSettings.GetScriptingDefineSymbolsForGroup(grp);
            if (cur.Split(';').Any(x => x.Trim() == d)) return;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(grp,
                string.IsNullOrEmpty(cur) ? d : cur + ";" + d);
#endif
            Debug.Log($"[VLab Setup] Scripting define '{d}' added.");
        }

        private static void RemoveDefine(string d)
        {
#if UNITY_6000_0_OR_NEWER
            var t = NamedBuildTarget.FromBuildTargetGroup(
                        BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            string cur = PlayerSettings.GetScriptingDefineSymbols(t);
            PlayerSettings.SetScriptingDefineSymbols(t,
                string.Join(";", cur.Split(';').Where(x => x.Trim() != d)));
#else
            var grp = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            string cur = PlayerSettings.GetScriptingDefineSymbolsForGroup(grp);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(grp,
                string.Join(";", cur.Split(';').Where(x => x.Trim() != d)));
#endif
        }
    }

    // =========================================================================
    //  EDITOR WINDOW
    // =========================================================================

    public class ProjectSetupWizard : EditorWindow
    {
        // ── Enums ─────────────────────────────────────────────────────────────
        public enum EduBoard { CBSE, ICSE, StateBoard }
        public enum Grade { Grade6, Grade7, Grade8, Grade9, Grade10, Grade11, Grade12 }
        public enum Subject { Physics, Chemistry, Biology, Mathematics }

        [Serializable]
        private class ModuleConfig
        {
            public string board, grade, subject, topic, createdDate;
        }

        // ── Constants ─────────────────────────────────────────────────────────
        private const string PACKAGE_ID = "com.unity.addressables";
        private const string MODULES_ROOT = "Assets/Modules";
        private const string GROUP_DEFAULT_LOCAL = "Default Local Group";
        private const string COMPANY_NAME = "mhcockpit";
        private const string JSON_CATALOG_DEFINE = "ENABLE_JSON_CATALOG";

        // Profile variable names.
        private const string PROFILE_BASE_URL = "CustomBaseURL";
        private const string PROFILE_REMOTE_BUILD = "Remote.BuildPath";
        private const string PROFILE_REMOTE_LOAD = "Remote.LoadPath";

        // Profile variable values
        private const string VALUE_BASE_URL = "http://localhost";
        private const string VALUE_REMOTE_BUILD_PATH = "ServerData/[BuildTarget]";
        private const string VALUE_REMOTE_LOAD_PATH = "{CustomBaseURL}/[BuildTarget]";

        // Provider type full names
        private const string PROVIDER_ASSET_BUNDLE =
            "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider, " +
            "Unity.ResourceManager";
        private const string PROVIDER_BUNDLED_ASSET =
            "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider, " +
            "Unity.ResourceManager";

        // ── Package state ─────────────────────────────────────────────────────
        private static ListRequest s_listReq;
        private static AddRequest s_addReq;
        private bool _checkingPkg;

        // ── Module folder fields ───────────────────────────────────────────────
        private EduBoard _board = EduBoard.CBSE;
        private Grade _grade = Grade.Grade12;
        private Subject _subject = Subject.Physics;
        private string _topic = string.Empty;

        // ── Step flags (7 steps) ──────────────────────────────────────────────
        private bool _s1, _s2, _s3, _s4, _s5, _s6, _s7;

        // ── Scene drag-and-drop fields (Step 6) ──────────────────────────────
        private SceneAsset _practiceScene;
        private SceneAsset _evaluationScene;

        // ── UI state ──────────────────────────────────────────────────────────
        private Vector2 _scroll;
        private string _msg = string.Empty;
        private bool _msgOk = true;

        // ── Styles ────────────────────────────────────────────────────────────
        private GUIStyle _styleSectionTitle;
        private GUIStyle _styleStepLabel;
        private GUIStyle _styleDone;
        private GUIStyle _stylePending;
        private GUIStyle _styleSmall;
        private GUIStyle _styleStatusOk;
        private GUIStyle _styleStatusWarn;
        private bool _stylesBuilt;

        private static readonly Color COL_GREEN = new Color(0.22f, 0.78f, 0.42f, 1f);
        private static readonly Color COL_ORANGE = new Color(0.95f, 0.72f, 0.15f, 1f);
        private static readonly Color COL_DIVIDER = new Color(0.32f, 0.32f, 0.32f, 1f);

        // ── Menu ──────────────────────────────────────────────────────────────
        [MenuItem("Tools/Virtual Lab/Project Setup", false, 0)]
        public static void Open()
        {
            var w = GetWindow<ProjectSetupWizard>(false, "Virtual Lab — Project Setup", true);
            w.minSize = new Vector2(420, 600);
            w.Show();
        }

        // ─────────────────────────────────────────────────────────────────────
        #region Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _stylesBuilt = false;
            _topic = Application.productName;
            RefreshFlags();
        }

        private void Update()
        {
            if (!_checkingPkg) return;
            bool dirty = false;

            if (s_listReq != null && s_listReq.IsCompleted)
            { OnListDone(); _checkingPkg = false; dirty = true; }
            else if (s_addReq != null && s_addReq.IsCompleted)
            { OnAddDone(); _checkingPkg = false; dirty = true; }

            if (dirty) Repaint();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region OnGUI
        // ─────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            BuildStyles();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(10);
            DrawHeader();
            GUILayout.Space(6);

#if !ADDRESSABLES_INSTALLED
            EditorGUILayout.HelpBox(
                "ADDRESSABLES_INSTALLED define is pending — Unity is still recompiling.\n" +
                "Wait a moment and this window will update automatically.",
                MessageType.Warning);
            GUILayout.Space(4);
#endif

            DrawStep(1, "Install Addressables",         _s1, true, DrawStep1Body);
            DrawStep(2, "Create Module Folder",          _s2, _s1,  DrawStep2Body);
            DrawStep(3, "Create Addressables Settings",  _s3, _s2,  DrawStep3Body);
            DrawStep(4, "Configure Profiles",            _s4, _s3,  DrawStep4Body);
            DrawStep(5, "Configure Default Local Group", _s5, _s4,  DrawStep5Body);
            DrawStep(6, "Add Scenes to Addressables",    _s6, _s5,  DrawStep6Body);
            DrawStep(7, "Save & Finish",                 _s7, _s6,  DrawStep7Body);

            GUILayout.Space(8);
            DrawStatusBar();
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            if (GUILayout.Button("Reset All Steps", GUILayout.Height(22)))
            {
                _s1 = _s2 = _s3 = _s4 = _s5 = _s6 = _s7 = false;
                Log("Steps reset.", true);
            }
            GUILayout.Space(12);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            EditorGUILayout.EndScrollView();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Layout Primitives
        // ─────────────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(48));
            EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f));
            GUI.Label(new Rect(r.x, r.y + 5, r.width, 26), "Virtual Lab", _styleSectionTitle);
            GUI.Label(new Rect(r.x, r.y + 28, r.width, 16), "Project Setup Wizard",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 10 });
        }

        private void DrawStep(int n, string title, bool done, bool unlocked, Action body)
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Height(30));
            GUILayout.Space(12);

            var badge = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));
            badge.y += 4;
            EditorGUI.DrawRect(badge,
                done ? COL_GREEN :
                unlocked ? COL_DIVIDER : new Color(0.18f, 0.18f, 0.18f));
            GUI.Label(badge, done ? "✓" : n.ToString(),
                new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 10,
                    normal = { textColor = done ? Color.black : Color.white }
                });

            GUILayout.Space(8);
            GUILayout.Label(title,
                done ? _styleDone :
                unlocked ? _styleStepLabel : _stylePending,
                GUILayout.ExpandWidth(true));
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            if (unlocked)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(42);
                EditorGUILayout.BeginVertical();
                body?.Invoke();
                GUILayout.Space(6);
                EditorGUILayout.EndVertical();
                GUILayout.Space(12);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(42);
                GUILayout.Label($"Complete Step {n - 1} to unlock.", _styleSmall);
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            var div = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(1), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(div, COL_DIVIDER);
        }

        private void RunBtn(int n, bool done, Action run, bool enabled = true)
        {
            EditorGUI.BeginDisabledGroup(!enabled);
            if (GUILayout.Button(done ? $"Re-run Step {n}" : $"Run Step {n}",
                GUILayout.Height(26), GUILayout.MaxWidth(200)))
                run?.Invoke();
            EditorGUI.EndDisabledGroup();
        }

        private void DrawStatusBar()
        {
            if (string.IsNullOrEmpty(_msg)) return;
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(32), GUILayout.ExpandWidth(true));
            r.x += 12; r.width -= 24;
            EditorGUI.DrawRect(r, _msgOk
                ? new Color(0.12f, 0.28f, 0.15f)
                : new Color(0.28f, 0.14f, 0.10f));
            GUI.Label(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 8),
                _msg, _msgOk ? _styleStatusOk : _styleStatusWarn);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Step Body Drawers
        // ─────────────────────────────────────────────────────────────────────

        private void DrawStep1Body()
        {
            GUILayout.Label("Verify com.unity.addressables is installed.", _styleSmall);
            GUILayout.Space(4);
            RunBtn(1, _s1, RunStep1, !_checkingPkg);
            if (_checkingPkg) GUILayout.Label("Working…", _styleSmall);
        }

        private void DrawStep2Body()
        {
            _board = (EduBoard)EditorGUILayout.EnumPopup("Board", _board);
            _grade = (Grade)EditorGUILayout.EnumPopup("Grade", _grade);
            _subject = (Subject)EditorGUILayout.EnumPopup("Subject", _subject);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Topic", _topic);
            EditorGUI.EndDisabledGroup();
            GUILayout.Space(4);
            if (GUILayout.Button("Create Module Folder",
                GUILayout.Height(26), GUILayout.MaxWidth(200)))
                RunStep2();
        }

        private void DrawStep3Body()
        {
            if (_s3)
            {
                GUILayout.Label("✓ Settings asset exists.", _styleDone);
            }
            else
            {
                GUILayout.Label(
                    "Creates Addressables Settings asset programmatically.\n" +
                    "No window will open — the wizard advances instantly.",
                    _styleSmall);
            }
            GUILayout.Space(4);
            RunBtn(3, _s3, RunStep3);
        }

        private void DrawStep4Body()
        {
            GUILayout.Label(
                "Sets CustomBaseURL   = http://localhost\n" +
                "Sets Remote.BuildPath = ServerData/[BuildTarget]\n" +
                "Sets Remote.LoadPath  = {CustomBaseURL}/[BuildTarget]",
                _styleSmall);
            GUILayout.Space(4);
            RunBtn(4, _s4, RunStep4);
        }

        private void DrawStep5Body()
        {
            GUILayout.Label(
                "Converts Default Local Group to Remote:\n" +
                "LZ4 · CRC · Append Hash · Pack Separately\n" +
                "Build & Load Paths → Remote preset\n" +
                "Asset Provider · Asset Bundle Provider · Catalog options",
                _styleSmall);
            GUILayout.Space(4);
            RunBtn(5, _s5, RunStep5);
        }

        private void DrawStep6Body()
        {
            GUILayout.Label(
                "Drag and drop your Practice and Evaluation scenes below.\n" +
                "If the scene file is not named \"Practice\" or \"Evaluation\",\n" +
                "it will be renamed automatically. Scenes outside the\n" +
                "module's Scenes folder will be moved there.",
                _styleSmall);
            GUILayout.Space(4);

            _practiceScene = (SceneAsset)EditorGUILayout.ObjectField(
                "Practice Scene", _practiceScene, typeof(SceneAsset), false);
            _evaluationScene = (SceneAsset)EditorGUILayout.ObjectField(
                "Evaluation Scene", _evaluationScene, typeof(SceneAsset), false);

            GUILayout.Space(4);
            RunBtn(6, _s6, RunStep6,
                enabled: _practiceScene != null && _evaluationScene != null);

            if (_practiceScene == null || _evaluationScene == null)
                GUILayout.Label("Assign both scenes to enable this step.", _styleSmall);
        }

        private void DrawStep7Body()
        {
            GUILayout.Label("Saves all assets and finalises setup.", _styleSmall);
            GUILayout.Space(4);
            RunBtn(7, _s7, RunStep7);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Step Logic
        // ─────────────────────────────────────────────────────────────────────

        // ── 1 — Install Addressables ──────────────────────────────────────────
        private void RunStep1()
        {
            Log("Checking package list…", true);
            _checkingPkg = true;
            s_listReq = Client.List(false, true);
        }

        private void OnListDone()
        {
            if (s_listReq.Status != StatusCode.Success)
            { Log($"Package list error: {s_listReq.Error?.message}", false); return; }

            bool found = s_listReq.Result
                .Any(p => p.name.Equals(PACKAGE_ID, StringComparison.OrdinalIgnoreCase));

            if (found) { _s1 = true; Log("Addressables already installed. ✓", true); }
            else
            {
                Log("Installing com.unity.addressables…", true);
                _checkingPkg = true;
                s_addReq = Client.Add(PACKAGE_ID);
            }
        }

        private void OnAddDone()
        {
            if (s_addReq.Status == StatusCode.Success)
            { _s1 = true; Log("Installed. Reopen this window after Unity recompiles. ✓", true); }
            else
                Log($"Install failed: {s_addReq.Error?.message}", false);
        }

        // ── 2 — Create Module Folder ──────────────────────────────────────────
        //   Only creates a single Scenes folder — no Practice/Evaluation subdirs.
        //   The user places scene files directly under Scenes/ and names them
        //   "Practice.unity" and "Evaluation.unity" (or whatever they prefer).
        private void RunStep2()
        {
            string root = Path.Combine(Application.dataPath, "Modules");
            string board = Path.Combine(root, _board.ToString());
            string grade = Path.Combine(board, _grade.ToString());
            string subj = Path.Combine(grade, _subject.ToString());
            string topic = Path.Combine(subj, _topic);

            if (Directory.Exists(topic))
            { _s2 = true; Log("Module folder already exists — continuing. ✓", true); return; }

            try
            {
                foreach (string d in new[]
                {
                    root, board, grade, subj, topic,
                    Path.Combine(topic, "Scripts"),
                    Path.Combine(topic, "Scenes")
                }) Directory.CreateDirectory(d);

                File.WriteAllText(Path.Combine(topic, "module_config.json"),
                    JsonUtility.ToJson(new ModuleConfig
                    {
                        board = _board.ToString(),
                        grade = _grade.ToString(),
                        subject = _subject.ToString(),
                        topic = _topic,
                        createdDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }, true));

                AssetDatabase.Refresh();
                _s2 = true;
                Log("Module folder created. ✓", true);
                Debug.Log($"[VLab Setup] Module folder: {topic}");
            }
            catch (Exception ex)
            {
                Log("Failed to create folders. See Console.", false);
                Debug.LogError($"[VLab Setup] {ex}");
            }
        }

        // ── 3 — Create Addressables Settings ──────────────────────────────────
        private void RunStep3()
        {
#if ADDRESSABLES_INSTALLED
            if (AddressableAssetSettingsDefaultObject.Settings != null)
            { _s3 = true; Log("Settings already exist. ✓", true); return; }

            try
            {
                var created = AddressableAssetSettings.Create(
                    AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                    AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName,
                    true,   // createRemoteCatalog
                    true);  // isPersisted

                if (created == null)
                {
                    Log("Failed to create Addressables Settings. See Console.", false);
                    Debug.LogError("[VLab Setup] AddressableAssetSettings.Create() returned null.");
                    return;
                }

                AddressableAssetSettingsDefaultObject.Settings = created;
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                _s3 = true;
                Log("Addressables Settings created. ✓", true);
                Debug.Log("[VLab Setup] Step 3 complete — Settings created programmatically.");

                OpenGroupsWindow();
            }
            catch (Exception ex)
            {
                Log("Exception creating Addressables Settings. See Console.", false);
                Debug.LogError($"[VLab Setup] Step 3 error: {ex}");
            }
#else
            Log("Addressables define not ready. If Step 1 is done, close and reopen this window.", false);
#endif
        }

        private static void OpenGroupsWindow() =>
            EditorApplication.ExecuteMenuItem("Window/Asset Management/Addressables/Groups");

        // ── 4 — Configure Profiles ────────────────────────────────────────────
        private void RunStep4()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;
            var p = s.profileSettings;

            SetProfileVar(p, PROFILE_BASE_URL, VALUE_BASE_URL);
            SetProfileVar(p, PROFILE_REMOTE_BUILD, VALUE_REMOTE_BUILD_PATH);
            SetProfileVar(p, PROFILE_REMOTE_LOAD, VALUE_REMOTE_LOAD_PATH);

            EditorUtility.SetDirty(s);
            _s4 = true;
            Log("Profiles configured. ✓", true);
            Debug.Log($"[VLab Setup] Profile variables set — " +
                      $"{PROFILE_BASE_URL}, {PROFILE_REMOTE_BUILD}, {PROFILE_REMOTE_LOAD}");
#else
            WarnNotReady();
#endif
        }

#if ADDRESSABLES_INSTALLED
        private static void SetProfileVar(AddressableAssetProfileSettings p, string name, string value)
        {
            if (!p.GetVariableNames().Contains(name))
            {
                p.CreateValue(name, value);
                Debug.Log($"[VLab Setup] Profile variable created: {name} = {value}");
                return;
            }
            string id = p.GetProfileId("Default");
            if (!string.IsNullOrEmpty(id))
            {
                p.SetValue(id, name, value);
                Debug.Log($"[VLab Setup] Profile variable updated: {name} = {value}");
            }
        }
#endif

        // ── 5 — Configure Default Local Group ─────────────────────────────────
        //   Converts the existing Default Local Group to use Remote build/load
        //   paths and applies all schema settings (LZ4, CRC, etc.).
        private void RunStep5()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            ApplyGlobalSettings(s);

            bool ok = ApplyGroupSchema(s, GROUP_DEFAULT_LOCAL);
            if (!ok) return;

            AssetDatabase.SaveAssets();

            _s5 = true;
            Log("Default Local Group configured as Remote. ✓", true);
            Debug.Log("[VLab Setup] Step 5 complete.");
#else
            WarnNotReady();
#endif
        }

#if ADDRESSABLES_INSTALLED
        private static void ApplyGlobalSettings(AddressableAssetSettings s)
        {
            s.OverridePlayerVersion = COMPANY_NAME;
            s.BuildRemoteCatalog = true;
            s.EnableJsonCatalog = true;
            s.ContiguousBundles = true;
            s.NonRecursiveBuilding = true;
            EnsureScriptingDefine(JSON_CATALOG_DEFINE);

            s.RemoteCatalogBuildPath.SetVariableByName(s, PROFILE_REMOTE_BUILD);
            s.RemoteCatalogLoadPath.SetVariableByName(s, PROFILE_REMOTE_LOAD);

            var so = new SerializedObject(s);
            so.Update();
            SetSerializedBoolAny(so, true, "logRuntimeExceptions", "m_BuildSettings.m_LogResourceManagerExceptions");
            SetSerializedInt(so, "m_InternalIdNamingMode", 0);  // Full Path
            SetSerializedInt(so, "m_InternalBundleIdMode", 2);  // Group Guid Project Id Hash
            SetSerializedInt(so, "m_MonoScriptBundleNaming", 1);  // Project Name Hash

            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(s);
            Debug.Log("[VLab Setup] Global settings applied — catalog paths set to Remote, JSON catalog enabled.");
        }

        private static bool ApplyGroupSchema(AddressableAssetSettings s, string groupName)
        {
            var group = s.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (group == null)
            {
                Debug.LogError($"[VLab Setup] Group '{groupName}' not found.");
                return false;
            }

            var b = group.GetSchema<BundledAssetGroupSchema>()
                 ?? group.AddSchema<BundledAssetGroupSchema>();

            // Build & Load Paths → Remote preset variables
            b.BuildPath.SetVariableByName(s, PROFILE_REMOTE_BUILD);
            b.LoadPath.SetVariableByName(s, PROFILE_REMOTE_LOAD);

            b.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            b.UseAssetBundleCrc = true;
            b.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
            b.UseAssetBundleCache = true;
            b.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            b.IncludeAddressInCatalog = true;
            b.IncludeGUIDInCatalog = false;

            var bso = new SerializedObject(b);
            bso.Update();
            SetSerializedManagedType(bso, "m_BundledAssetProviderType", PROVIDER_BUNDLED_ASSET);
            SetSerializedManagedType(bso, "m_AssetBundleProviderType", PROVIDER_ASSET_BUNDLE);
            SetSerializedBool(bso, "m_IncludeLabelsInCatalog", false);
            bso.ApplyModifiedPropertiesWithoutUndo();

            var cu = group.GetSchema<ContentUpdateGroupSchema>()
                  ?? group.AddSchema<ContentUpdateGroupSchema>();
            cu.StaticContent = false;

            EditorUtility.SetDirty(group);
            Debug.Log($"[VLab Setup] Schema applied to '{groupName}' — Build & Load Paths → Remote.");
            return true;
        }

        // ── SerializedObject helpers ───────────────────────────────────────────

        private static void SetSerializedBool(SerializedObject so, string field, bool v)
        {
            var p = so.FindProperty(field);
            if (p != null) p.boolValue = v;
            else Debug.LogWarning($"[VLab Setup] SerializedProperty '{field}' not found — skipping.");
        }

        private static void SetSerializedBoolAny(SerializedObject so, bool value, params string[] fields)
        {
            foreach (string field in fields)
            {
                var p = so.FindProperty(field);
                if (p == null) continue;
                p.boolValue = value;
                return;
            }
        }

        private static void SetSerializedInt(SerializedObject so, string field, int v)
        {
            var p = so.FindProperty(field);
            if (p != null) p.intValue = v;
            else Debug.LogWarning($"[VLab Setup] SerializedProperty '{field}' not found — skipping.");
        }

        private static void SetSerializedManagedType(SerializedObject so, string field, string aqn)
        {
            var typeProp = so.FindProperty(field);
            if (typeProp == null)
            { Debug.LogWarning($"[VLab Setup] SerializedProperty '{field}' not found — skipping."); return; }

            var child = typeProp.FindPropertyRelative("m_AssemblyQualifiedName");
            if (child != null)
            {
                child.stringValue = aqn;
                return;
            }

            var classProp = typeProp.FindPropertyRelative("m_ClassName");
            var asmProp = typeProp.FindPropertyRelative("m_AssemblyName");
            if (classProp != null && asmProp != null)
            {
                int comma = aqn.IndexOf(',');
                if (comma > 0)
                {
                    classProp.stringValue = aqn.Substring(0, comma).Trim();
                    asmProp.stringValue = aqn.Substring(comma + 1).Trim();
                }
                else
                {
                    classProp.stringValue = aqn.Trim();
                    asmProp.stringValue = string.Empty;
                }
                return;
            }

            var it = typeProp.Copy();
            bool nxt = it.Next(true);
            while (nxt)
            {
                if (it.propertyType == SerializedPropertyType.String &&
                    it.name.ToLower().Contains("type"))
                { it.stringValue = aqn; return; }
                nxt = it.Next(false);
            }

            Debug.LogWarning($"[VLab Setup] Could not find type sub-property on '{field}'.");
        }

        private static bool EnsureScriptingDefine(string define)
        {
#if UNITY_6000_0_OR_NEWER
            var target = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            string current = PlayerSettings.GetScriptingDefineSymbols(target);
            if (current.Split(';').Any(x => x.Trim() == define))
                return false;

            string updated = string.IsNullOrEmpty(current) ? define : current + ";" + define;
            PlayerSettings.SetScriptingDefineSymbols(target, updated);
            return true;
#else
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            string current = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            if (current.Split(';').Any(x => x.Trim() == define))
                return false;

            string updated = string.IsNullOrEmpty(current) ? define : current + ";" + define;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, updated);
            return true;
#endif
        }
#endif

        // ── 6 — Add Scenes to Addressables ────────────────────────────────────
        //   The user drags Practice and Evaluation scenes into ObjectFields.
        //   If the scene file name does not match, it is renamed.
        //   If the scene is not inside the module's Scenes folder, it is moved.
        //   Both scenes are then added to the Default Local Group with the
        //   scene file name (without extension) as the addressable key.
        private void RunStep6()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            if (_practiceScene == null || _evaluationScene == null)
            { Log("Assign both Practice and Evaluation scenes first.", false); return; }

            if (_practiceScene == _evaluationScene)
            { Log("Practice and Evaluation must be different scenes.", false); return; }

            // Locate the module's Scenes folder created by Step 2.
            string scenesFolder = FindModuleScenesFolder();
            if (scenesFolder == null)
            { Log("Module Scenes folder not found. Run Step 2 first.", false); return; }

            var group = s.groups.FirstOrDefault(g => g != null && g.Name == GROUP_DEFAULT_LOCAL);
            if (group == null)
            { Log($"'{GROUP_DEFAULT_LOCAL}' not found — run Step 3 first.", false); return; }

            // Process each scene: rename if needed, move if needed, register.
            string practiceResult = ProcessScene(s, group, _practiceScene, "Practice", scenesFolder);
            if (practiceResult == null) return;

            string evalResult = ProcessScene(s, group, _evaluationScene, "Evaluation", scenesFolder);
            if (evalResult == null) return;

            s.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            EditorUtility.SetDirty(s);

            // Re-acquire references after possible renames/moves.
            _practiceScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(practiceResult);
            _evaluationScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(evalResult);

            _s6 = true;
            Log("Practice and Evaluation scenes added to Default Local Group. ✓", true);
            Debug.Log("[VLab Setup] Step 6 complete — scenes registered in Addressables.");
#else
            WarnNotReady();
#endif
        }

#if ADDRESSABLES_INSTALLED
        /// <summary>
        /// Ensures a scene asset has the correct name, lives in the Scenes folder,
        /// and is registered in the given Addressable group.
        /// Returns the final asset path on success, null on failure.
        /// </summary>
        private string ProcessScene(
            AddressableAssetSettings settings,
            AddressableAssetGroup group,
            SceneAsset scene,
            string requiredName,
            string scenesFolder)
        {
            string path = AssetDatabase.GetAssetPath(scene);
            if (string.IsNullOrEmpty(path))
            { Log($"Could not resolve path for {requiredName} scene.", false); return null; }

            string currentName = Path.GetFileNameWithoutExtension(path);

            // ── 1. Rename if the file is not already named correctly ─────────
            if (!currentName.Equals(requiredName, StringComparison.Ordinal))
            {
                string renameErr = AssetDatabase.RenameAsset(path, requiredName);
                if (!string.IsNullOrEmpty(renameErr))
                {
                    Log($"Failed to rename '{currentName}' → '{requiredName}': {renameErr}", false);
                    Debug.LogError($"[VLab Setup] Rename error: {renameErr}");
                    return null;
                }

                // Update path after rename.
                path = Path.Combine(Path.GetDirectoryName(path), requiredName + ".unity")
                    .Replace('\\', '/');
                Debug.Log($"[VLab Setup] Renamed scene: '{currentName}' → '{requiredName}'");
            }

            // ── 2. Move into Scenes folder if not already there ──────────────
            // scenesFolder is an asset-relative path like "Assets/Modules/CBSE/…/Scenes"
            string currentDir = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!currentDir.Equals(scenesFolder, StringComparison.OrdinalIgnoreCase))
            {
                string destPath = scenesFolder + "/" + requiredName + ".unity";

                // If a file already exists at the destination, remove it first.
                if (File.Exists(Path.GetFullPath(
                        Path.Combine(Application.dataPath, "..", destPath))))
                {
                    AssetDatabase.DeleteAsset(destPath);
                }

                string moveErr = AssetDatabase.MoveAsset(path, destPath);
                if (!string.IsNullOrEmpty(moveErr))
                {
                    Log($"Failed to move '{requiredName}' to Scenes folder: {moveErr}", false);
                    Debug.LogError($"[VLab Setup] Move error: {moveErr}");
                    return null;
                }

                path = destPath;
                Debug.Log($"[VLab Setup] Moved scene to: {path}");
            }

            AssetDatabase.Refresh();

            // ── 3. Register in Addressable group ─────────────────────────────
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            { Log($"Could not resolve GUID for '{path}'.", false); return null; }

            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            if (entry == null)
            { Log($"Failed to create Addressable entry for '{requiredName}'.", false); return null; }

            entry.address = requiredName;
            Debug.Log($"[VLab Setup] Scene '{path}' → key: '{requiredName}' → {group.Name}");

            return path;
        }
#endif

        /// <summary>
        /// Searches Assets/Modules for the first directory named "Scenes" and
        /// returns its asset-relative path (e.g. "Assets/Modules/CBSE/…/Scenes").
        /// Returns null if not found.
        /// </summary>
        private static string FindModuleScenesFolder()
        {
            string modulesAbs = Path.Combine(Application.dataPath, "Modules");
            if (!Directory.Exists(modulesAbs)) return null;

            string[] dirs = Directory.GetDirectories(
                modulesAbs, "Scenes", SearchOption.AllDirectories);

            if (dirs.Length == 0) return null;

            // Convert absolute path back to asset-relative path.
            string assetsRoot = Application.dataPath; // ends with /Assets
            string rel = "Assets" + dirs[0].Substring(assetsRoot.Length).Replace('\\', '/');
            return rel;
        }

        // ── 7 — Save & Finish ─────────────────────────────────────────────────
        private const string SETUP_DONE_MARKER =
            "Assets/AddressableAssetsData/vlabsetup.done";

        private void RunStep7()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string markerAbs = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SETUP_DONE_MARKER));
            File.WriteAllText(markerAbs, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AssetDatabase.ImportAsset(SETUP_DONE_MARKER);

            _s7 = true;
            Log("Setup complete! All assets saved. ✓", true);
            Debug.Log("[VLab Setup] Complete.");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Styles
        // ─────────────────────────────────────────────────────────────────────

        private void BuildStyles()
        {
            if (_stylesBuilt) return;

            _styleSectionTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            _styleStepLabel = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.88f, 0.88f, 0.88f) }
            };
            _styleDone = new GUIStyle(_styleStepLabel)
            { normal = { textColor = COL_GREEN } };
            _stylePending = new GUIStyle(_styleStepLabel)
            { normal = { textColor = new Color(0.45f, 0.45f, 0.45f) } };
            _styleSmall = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            { normal = { textColor = new Color(0.60f, 0.60f, 0.60f) } };
            _styleStatusOk = new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = COL_GREEN }, wordWrap = true };
            _styleStatusWarn = new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = COL_ORANGE }, wordWrap = true };

            _stylesBuilt = true;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Utilities
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshFlags()
        {
#if ADDRESSABLES_INSTALLED
            _s1 = true;
            _s2 = Directory.Exists(Path.Combine(Application.dataPath, "Modules"));

            var s = AddressableAssetSettingsDefaultObject.Settings;
            _s3 = s != null;

            if (s != null)
            {
                // Step 4: all three profile variables must exist.
                var pn = s.profileSettings.GetVariableNames();
                _s4 = pn.Contains(PROFILE_BASE_URL)
                   && pn.Contains(PROFILE_REMOTE_BUILD)
                   && pn.Contains(PROFILE_REMOTE_LOAD);

                // Step 5: Default Local Group must have schema configured with Remote paths.
                _s5 = _s4 && IsGroupSchemaConfigured(s, GROUP_DEFAULT_LOCAL);

                // Step 6: both Practice and Evaluation entries must exist.
                _s6 = _s5 && HasAddressableSceneEntries(s);

                // Re-populate the drag-drop fields from existing entries so the
                // user sees them on window reopen.
                if (_s6)
                {
                    var dlg = s.groups.FirstOrDefault(g => g?.Name == GROUP_DEFAULT_LOCAL);
                    if (dlg != null)
                    {
                        foreach (var e in dlg.entries)
                        {
                            if (e.address == "Practice")
                                _practiceScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
                                    AssetDatabase.GUIDToAssetPath(e.guid));
                            else if (e.address == "Evaluation")
                                _evaluationScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
                                    AssetDatabase.GUIDToAssetPath(e.guid));
                        }
                    }
                }

                // Step 7: completion marker file.
                _s7 = _s6 && File.Exists(
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..", SETUP_DONE_MARKER)));
            }
#else
            _s1 = false;
            _s2 = Directory.Exists(Path.Combine(Application.dataPath, "Modules"));
#endif
        }

#if ADDRESSABLES_INSTALLED
        private static bool IsGroupSchemaConfigured(AddressableAssetSettings s, string groupName)
        {
            var group = s.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (group == null) return false;

            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null) return false;

            return schema.BuildPath.GetName(s) == PROFILE_REMOTE_BUILD;
        }

        private static bool HasAddressableSceneEntries(AddressableAssetSettings s)
        {
            var group = s.groups.FirstOrDefault(g => g != null && g.Name == GROUP_DEFAULT_LOCAL);
            if (group == null) return false;

            bool hasPractice = group.entries.Any(e => e.address == "Practice");
            bool hasEval = group.entries.Any(e => e.address == "Evaluation");
            return hasPractice && hasEval;
        }
#endif

        /// <summary>
        /// Converts a raw topic string to a PascalCase key.
        /// Retained for use by BuildAndUploadToS3 when constructing S3 paths.
        /// </summary>
        internal static string ToAddressableKey(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return sceneName;

            string[] words = sceneName.Split(
                new[] { ' ', '-', '_', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            var sb = new StringBuilder(sceneName.Length);
            foreach (string word in words)
            {
                var clean = new StringBuilder(word.Length);
                foreach (char c in word)
                    if (char.IsLetterOrDigit(c)) clean.Append(c);

                if (clean.Length == 0) continue;

                sb.Append(char.ToUpperInvariant(clean[0]));
                if (clean.Length > 1) sb.Append(clean.ToString(1, clean.Length - 1));
            }

            return sb.ToString();
        }

        private void Log(string msg, bool ok) { _msg = msg; _msgOk = ok; Repaint(); }

        private void WarnNotReady() =>
            Log("Addressables not ready — close and reopen this window if Step 1 is done.", false);

#if ADDRESSABLES_INSTALLED
        private AddressableAssetSettings GetSettings()
        {
            var s = AddressableAssetSettingsDefaultObject.Settings;
            if (s == null) Log("Addressables Settings missing. Complete Step 3 first.", false);
            return s;
        }
#endif

        #endregion
    }
}

#endif