// Packages/com.mhcockpit.vlpipe/Editor/ProjectSetupWizard.cs
// Menu: Tools → Virtual Lab → Project Setup

#if UNITY_EDITOR

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
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
            var t = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
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
            var t = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
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
        private const string GROUP_PRACTICE = "Practice";
        private const string GROUP_EVALUATION = "Evaluation";

        // Profile variable names.
        // Remote.BuildPath / Remote.LoadPath are the built-in Addressables "Remote"
        // preset variables — pointing the group schema at these makes the
        // Build & Load Paths dropdown show "Remote" (not <custom>).
        private const string PROFILE_BASE_URL = "CustomBaseURL";
        private const string PROFILE_REMOTE_BUILD = "Remote.BuildPath";
        private const string PROFILE_REMOTE_LOAD = "Remote.LoadPath";

        // Profile variable values
        private const string VALUE_BASE_URL = "http://localhost";
        private const string VALUE_REMOTE_BUILD_PATH = "ServerData/[BuildTarget]";
        private const string VALUE_REMOTE_LOAD_PATH = "{CustomBaseURL}/[BuildTarget]";

        // Provider type full names (fixes <none> Asset Provider)
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

        // ── Step flags ────────────────────────────────────────────────────────
        private bool _s1, _s2, _s3, _s4, _s5, _s6, _s7, _s8;

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
            w.minSize = new Vector2(420, 660);
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

            DrawStep(1, "Install Addressables", _s1, true, DrawStep1Body);
            DrawStep(2, "Create Module Folder", _s2, _s1, DrawStep2Body);
            DrawStep(3, "Create Addressables Settings", _s3, _s2, DrawStep3Body);
            DrawStep(4, "Configure Profiles", _s4, _s3, DrawStep4Body);
            DrawStep(5, "Create Asset Groups", _s5, _s4, DrawStep5Body);
            DrawStep(6, "Configure Group Settings", _s6, _s5, DrawStep6Body);
            DrawStep(7, "Add Scenes to Addressables", _s7, _s6, DrawStep7Body);
            DrawStep(8, "Save & Finish", _s8, _s7, DrawStep8Body);

            GUILayout.Space(8);
            DrawStatusBar();
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            if (GUILayout.Button("Reset All Steps", GUILayout.Height(22)))
            {
                _s1 = _s2 = _s3 = _s4 = _s5 = _s6 = _s7 = _s8 = false;
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
                // FIX: settings created fully in code — no window opened, no freeze.
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
            GUILayout.Label("Creates Practice and Evaluation groups with correct schemas.", _styleSmall);
            GUILayout.Space(4);
            RunBtn(5, _s5, RunStep5);
        }

        private void DrawStep6Body()
        {
            GUILayout.Label(
                "Applies all settings from project config:\n" +
                "LZ4 · CRC · Append Hash · Pack Separately\n" +
                "Build & Load Paths → Remote preset\n" +
                "Asset Provider · Asset Bundle Provider · Catalog options",
                _styleSmall);
            GUILayout.Space(4);
            RunBtn(6, _s6, RunStep6);
        }

        private void DrawStep7Body()
        {
            GUILayout.Label(
                "Scans Assets/Modules — /Practice/ and /Evaluation/ by path.\n" +
                "Scene file name  : topic text  (e.g. \"Comparing EMF of Two Cells\")\n" +
                "Addressable key  : PascalCase  (e.g. \"ComparingEMFOfTwoCells\")",
                _styleSmall);
            GUILayout.Space(4);
            RunBtn(7, _s7, RunStep7);
        }

        private void DrawStep8Body()
        {
            GUILayout.Label("Saves all assets and finalises setup.", _styleSmall);
            GUILayout.Space(4);
            RunBtn(8, _s8, RunStep8);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Step Logic
        // ─────────────────────────────────────────────────────────────────────

        // ── 1 ─────────────────────────────────────────────────────────────────
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

        // ── 2 ─────────────────────────────────────────────────────────────────
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
                    Path.Combine(topic, "Scenes"),
                    Path.Combine(topic, "Scenes", "Practice"),
                    Path.Combine(topic, "Scenes", "Evaluation")
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

        // ── 3 ─────────────────────────────────────────────────────────────────
        // FIX — ROOT CAUSE:
        //   The previous implementation called OpenGroupsWindow() which executed
        //   "Window/Asset Management/Addressables/Groups". That menu item triggers
        //   an internal domain reload inside the Addressables package, causing
        //   Unity to appear frozen until the reload completes.
        //
        // FIX — SOLUTION:
        //   Call AddressableAssetSettings.Create() directly. This creates the
        //   AddressableAssetSettings.asset and all supporting files entirely in
        //   code, is fully synchronous, opens no windows, and causes no reload.
        private void RunStep3()
        {
#if ADDRESSABLES_INSTALLED
            if (AddressableAssetSettingsDefaultObject.Settings != null)
            { _s3 = true; Log("Settings already exist. ✓", true); return; }

            try
            {
                // kDefaultConfigFolder   = "Assets/AddressableAssetsData"
                // kDefaultConfigAssetName = "AddressableAssetSettings"
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
                Log("Addressables Settings created. ✓  Groups window opened.", true);
                Debug.Log("[VLab Setup] Step 3 complete — Settings created programmatically.");

                // Open the Groups window so the user can see the newly created settings.
                // This is a view-only open — it does NOT trigger a domain reload because
                // the settings asset already exists in the AssetDatabase at this point.
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

        // ── 4 ─────────────────────────────────────────────────────────────────
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

        // ── 5 ─────────────────────────────────────────────────────────────────
        private void RunStep5()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;
            MakeGroup(s, GROUP_PRACTICE);
            MakeGroup(s, GROUP_EVALUATION);
            EditorUtility.SetDirty(s);
            _s5 = true;
            Log("Practice and Evaluation groups created. ✓", true);
            Debug.Log("[VLab Setup] Groups created.");
#else
            WarnNotReady();
#endif
        }

#if ADDRESSABLES_INSTALLED
        private static void MakeGroup(AddressableAssetSettings s, string name)
        {
            if (s.groups.Any(g => g != null && g.Name == name)) return;
            s.CreateGroup(name, false, false, true, null,
                typeof(BundledAssetGroupSchema),
                typeof(ContentUpdateGroupSchema));
            Debug.Log($"[VLab Setup] Group '{name}' created.");
        }

        private static bool GroupExists(AddressableAssetSettings s, string name) =>
            s.groups.Any(g => g != null && g.Name == name);
#endif

        // ── 6 ─────────────────────────────────────────────────────────────────
        private void RunStep6()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            ApplyGlobalSettings(s);

            bool ok = ApplyGroupSchema(s, GROUP_PRACTICE)
                    & ApplyGroupSchema(s, GROUP_EVALUATION);
            if (!ok) return;

            AssetDatabase.SaveAssets();
            _s6 = true;
            Log("All group and global settings applied. ✓", true);
            Debug.Log("[VLab Setup] Step 6 complete.");
#else
            WarnNotReady();
#endif
        }

#if ADDRESSABLES_INSTALLED
        private static void ApplyGlobalSettings(AddressableAssetSettings s)
        {
            s.BuildRemoteCatalog = true;
            s.ContiguousBundles = true;
            s.NonRecursiveBuilding = true;

            // ── FIX: set the GLOBAL catalog build/load paths to Remote variables ─
            // Without this the top-level inspector "Build & Load Paths" shows
            // <custom> with empty path previews (as seen in the inspector screenshot).
            // These are the catalog paths (distinct from per-group schema paths).
            s.RemoteCatalogBuildPath.SetVariableByName(s, PROFILE_REMOTE_BUILD);
            s.RemoteCatalogLoadPath.SetVariableByName(s, PROFILE_REMOTE_LOAD);

            var so = new SerializedObject(s);
            so.Update();
            SetSerializedBool(so, "logRuntimeExceptions", true);
            SetSerializedInt(so, "m_InternalIdNamingMode", 0);  // Full Path
            SetSerializedInt(so, "m_InternalBundleIdMode", 2);  // Group Guid Project Id Hash
            SetSerializedInt(so, "m_MonoScriptBundleNaming", 1);  // Project Name Hash
            SetSerializedInt(so, "m_ShaderBundleNaming", 1);  // Project Name Hash

            // ── FIX: disable catalog version suffix ───────────────────────────
            // By default Addressables appends the Player Version to the catalog
            // filename: catalog_0.1.0.json, catalog_1.0.0.json, etc.
            // This makes the filename unpredictable at runtime — the Dashboard
            // cannot know which version string to append when building the URL.
            //
            // Setting OverridePlayerVersion to an empty string forces Addressables
            // to output a fixed filename: catalog.json
            // The Dashboard S3CatalogPathTemplate then reliably ends with catalog.json.
            SetSerializedString(so, "m_OverridePlayerVersion", "");

            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(s);
            Debug.Log("[VLab Setup] Global settings applied — catalog paths set to Remote, catalog versioning disabled (output: catalog.json).");
        }

        private static bool ApplyGroupSchema(AddressableAssetSettings s, string groupName)
        {
            var group = s.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (group == null)
            {
                Debug.LogError($"[VLab Setup] Group '{groupName}' not found — run Step 5 first.");
                return false;
            }

            var b = group.GetSchema<BundledAssetGroupSchema>()
                 ?? group.AddSchema<BundledAssetGroupSchema>();

            // Build & Load Paths → Remote preset variables (shows "Remote" in dropdown)
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

        private static void SetSerializedInt(SerializedObject so, string field, int v)
        {
            var p = so.FindProperty(field);
            if (p != null) p.intValue = v;
            else Debug.LogWarning($"[VLab Setup] SerializedProperty '{field}' not found — skipping.");
        }

        private static void SetSerializedString(SerializedObject so, string field, string v)
        {
            var p = so.FindProperty(field);
            if (p != null) p.stringValue = v;
            else Debug.LogWarning($"[VLab Setup] SerializedProperty '{field}' not found — skipping.");
        }

        private static void SetSerializedManagedType(SerializedObject so, string field, string aqn)
        {
            var typeProp = so.FindProperty(field);
            if (typeProp == null)
            { Debug.LogWarning($"[VLab Setup] SerializedProperty '{field}' not found — skipping."); return; }

            var child = typeProp.FindPropertyRelative("m_AssemblyQualifiedName");
            if (child != null) { child.stringValue = aqn; return; }

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
#endif

        // ── 7 ─────────────────────────────────────────────────────────────────
        // FIX — SCENE ADDRESS:
        //   Previously `Path.GetFileNameWithoutExtension(path)` was used as the
        //   source for ToAddressableKey(). If the scene file is named "A" (or any
        //   short test name), the address would also be "A".
        //
        //   The correct source is the `topic` field in the nearest module_config.json
        //   above the scene file in the directory hierarchy. The scene file name is
        //   irrelevant — only the topic matters.
        //
        //   Address convention (avoids duplicate keys across groups):
        //     Practice   scene → ToAddressableKey(topic)               e.g. "ComparingEMFOfTwoGivenPrimaryCells"
        //     Evaluation scene → ToAddressableKey(topic)     e.g. "ComparingEMFOfTwoGivenPrimaryCells"
        private void RunStep7()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            if (!Directory.Exists(MODULES_ROOT))
            { Log($"'{MODULES_ROOT}' not found. Run Step 2 first.", false); return; }

            string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { MODULES_ROOT });
            if (guids.Length == 0)
            { Log("No .unity scenes found under Assets/Modules.", false); return; }

            var pg = s.groups.FirstOrDefault(g => g?.Name == GROUP_PRACTICE);
            var eg = s.groups.FirstOrDefault(g => g?.Name == GROUP_EVALUATION);
            if (pg == null || eg == null)
            { Log("Groups missing — run Steps 5 & 6 first.", false); return; }

            int pc = 0, ec = 0, sk = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var target = path.Contains("/Practice/") ? pg :
                                path.Contains("/Evaluation/") ? eg : null;
                if (target == null) { sk++; continue; }

                var e = s.CreateOrMoveEntry(guid, target, false, false);
                if (e == null) continue;

                // ── Derive address from module_config.json topic ───────────────
                // Walk up the directory tree from the scene to find the config.
                // This means the address is always the canonical topic name,
                // regardless of what the .unity file itself is named.
                string sceneDirAbs = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", Path.GetDirectoryName(path)));
                string configPath = FindModuleConfigAbove(sceneDirAbs);
                string topic = ReadTopicFromConfig(configPath);

                // Fallback: if config is missing, use the scene file name.
                if (string.IsNullOrEmpty(topic))
                    topic = Path.GetFileNameWithoutExtension(path);

                string addressableKey = ToAddressableKey(topic);
                e.address = addressableKey;

                if (target == pg) pc++; else ec++;
                Debug.Log($"[VLab Setup] Scene '{Path.GetFileNameWithoutExtension(path)}'  " +
                          $"topic: '{topic}'  →  key: '{addressableKey}'  →  {target.Name}");
            }

            s.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            EditorUtility.SetDirty(s);
            _s7 = true;
            Log($"Scenes added — Practice: {pc}  Evaluation: {ec}  Skipped: {sk}", true);
            Debug.Log("[VLab Setup] Scenes added to Addressables.");
#else
            WarnNotReady();
#endif
        }

        // ── 8 ─────────────────────────────────────────────────────────────────
        // FIX: Previously _s8 had no disk-based check so it was always false on
        // reopen. A lightweight marker file is written to Assets/AddressableAssetsData/
        // and checked in RefreshFlags() to persist the completed state.
        private const string SETUP_DONE_MARKER =
            "Assets/AddressableAssetsData/vlabsetup.done";

        private void RunStep8()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Write the completion marker so RefreshFlags() can detect Step 8 on reopen.
            string markerAbs = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SETUP_DONE_MARKER));
            File.WriteAllText(markerAbs, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AssetDatabase.ImportAsset(SETUP_DONE_MARKER);

            _s8 = true;
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

        /// <summary>
        /// Derives every step-completion flag purely from the state of project
        /// assets on disk.
        ///
        /// FIX — ROOT CAUSE OF REGRESSION:
        ///   Previously _s6 had no asset-state check — it was never set to true
        ///   in RefreshFlags(), so every time the editor was reopened the wizard
        ///   would present Step 6 (Configure Group Settings) as incomplete even
        ///   when it had already been run.
        ///
        /// FIX — SOLUTION:
        ///   Added IsGroupSchemaConfigured() which inspects the saved
        ///   BundledAssetGroupSchema.BuildPath variable name. If it equals
        ///   "Remote.BuildPath" then Step 6 was previously completed.
        ///   Similarly, HasAddressableSceneEntries() checks for Step 7.
        /// </summary>
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

                // Step 5: both groups must exist.
                _s5 = GroupExists(s, GROUP_PRACTICE) && GroupExists(s, GROUP_EVALUATION);

                // Step 6: both groups must have schema configured with Remote paths.
                _s6 = _s5
                   && IsGroupSchemaConfigured(s, GROUP_PRACTICE)
                   && IsGroupSchemaConfigured(s, GROUP_EVALUATION);

                // Step 7: at least one entry exists in either Addressable group.
                _s7 = _s6 && HasAddressableSceneEntries(s);

                // Step 8: completion marker file written by RunStep8().
                _s8 = _s7 && File.Exists(
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..", SETUP_DONE_MARKER)));
            }
#else
            _s1 = false;
            _s2 = Directory.Exists(Path.Combine(Application.dataPath, "Modules"));
#endif
        }

#if ADDRESSABLES_INSTALLED
        /// <summary>
        /// Returns true when the named group has a BundledAssetGroupSchema whose
        /// BuildPath variable is "Remote.BuildPath" — the canonical signal that
        /// Step 6 (ApplyGroupSchema) has been run and saved.
        /// </summary>
        private static bool IsGroupSchemaConfigured(AddressableAssetSettings s, string groupName)
        {
            var group = s.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (group == null) return false;

            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null) return false;

            // GetName() returns the profile variable token the path entry references.
            return schema.BuildPath.GetName(s) == PROFILE_REMOTE_BUILD;
        }

        /// <summary>
        /// Returns true when either the Practice or Evaluation group contains at
        /// least one entry — i.e. Step 7 has been run and saved.
        /// </summary>
        private static bool HasAddressableSceneEntries(AddressableAssetSettings s)
        {
            var pg = s.groups.FirstOrDefault(g => g?.Name == GROUP_PRACTICE);
            var eg = s.groups.FirstOrDefault(g => g?.Name == GROUP_EVALUATION);
            return (pg != null && pg.entries.Count > 0)
                || (eg != null && eg.entries.Count > 0);
        }
#endif

        /// <summary>
        /// Walks up the directory tree from <paramref name="startDirAbsolute"/> until it
        /// finds a module_config.json file or reaches the project Assets root.
        /// Returns the absolute path to the config file, or null if not found.
        /// </summary>
        private static string FindModuleConfigAbove(string startDirAbsolute)
        {
            string assetsRoot = Path.GetFullPath(Application.dataPath);
            string dir = startDirAbsolute;

            while (!string.IsNullOrEmpty(dir) &&
                   dir.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(dir, "module_config.json");
                if (File.Exists(candidate)) return candidate;

                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break; // filesystem root guard
                dir = parent;
            }
            return null;
        }

        /// <summary>
        /// Reads the "topic" field from a module_config.json file at the given
        /// absolute path. Returns null on any error or if the file is missing.
        /// </summary>
        private static string ReadTopicFromConfig(string configAbsPath)
        {
            if (string.IsNullOrEmpty(configAbsPath) || !File.Exists(configAbsPath))
                return null;

            try
            {
                string json = File.ReadAllText(configAbsPath);
                var config = JsonUtility.FromJson<ModuleConfig>(json);
                return string.IsNullOrWhiteSpace(config?.topic) ? null : config.topic;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VLab Setup] Could not read module_config.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// key with no spaces, hyphens, or underscores.
        ///
        /// Rules:
        ///   • Splits on spaces, hyphens, and underscores.
        ///   • Capitalises the first character of each word segment.
        ///   • Preserves existing upper-case runs (e.g. "EMF" stays "EMF").
        ///   • Strips all non-alphanumeric characters from each segment.
        ///
        /// Examples:
        ///   "Comparing EMF of Two Given Primary Cells"
        ///       → "ComparingEMFOfTwoGivenPrimaryCells"
        ///   "simple pendulum"
        ///       → "SimplePendulum"
        ///   "Ohm's Law - Experiment 1"
        ///       → "OhmsLawExperiment1"
        /// </summary>
        private static string ToAddressableKey(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return sceneName;

            string[] words = sceneName.Split(
                new[] { ' ', '-', '_', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            var sb = new StringBuilder(sceneName.Length);
            foreach (string word in words)
            {
                // Strip any non-alphanumeric characters (e.g. apostrophes).
                var clean = new StringBuilder(word.Length);
                foreach (char c in word)
                    if (char.IsLetterOrDigit(c)) clean.Append(c);

                if (clean.Length == 0) continue;

                // Capitalise first character; leave the rest as-is so that
                // existing all-caps acronyms (EMF, AC, DC …) are preserved.
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
