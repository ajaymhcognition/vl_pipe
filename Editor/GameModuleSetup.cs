// ============================================================================
//  GameModuleSetup.cs
//  Menu: Tools → Game Module → Project Setup
//
//  Unity Editor wizard that configures a game-module project for Addressable
//  remote scene loading from AWS S3.
//
//  Target:  Unity 6.3 LTS+  ·  Addressables 2.8.1+  ·  URP 17.3.0+
//  Layout:  Assets/Modules/{Board}/Grade{N}/{Subject}/{Topic}/
//               Practice.unity
//               Evaluation.unity
//               module_config.json
//
//  Addressable addresses:
//      Practice.unity   → "practice"
//      Evaluation.unity → "evaluation"
//
//  Single group: "RemoteScenes" (renamed from Default Local Group)
// ============================================================================

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
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

#if ADDRESSABLES_INSTALLED
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
#endif

namespace MHCockpit.VLPipe.Editor
{
    // ========================================================================
    //  BOOTSTRAPPER — auto-manages ADDRESSABLES_INSTALLED scripting define
    // ========================================================================

    [InitializeOnLoad]
    internal static class AddressablesDefineBootstrapper
    {
        private const string DEFINE   = "ADDRESSABLES_INSTALLED";
        private const string EDITOR_ASM = "Unity.Addressables.Editor";

        static AddressablesDefineBootstrapper()
        {
            bool present = AppDomain.CurrentDomain
                .GetAssemblies()
                .Any(a => a.GetName().Name
                    .Equals(EDITOR_ASM, StringComparison.OrdinalIgnoreCase));

            if (present) EnsureDefine(DEFINE);
            else         RemoveDefine(DEFINE);
        }

        private static void EnsureDefine(string d)
        {
            string cur = GetDefines();
            if (cur.Split(';').Any(x => x.Trim() == d)) return;
            SetDefines(string.IsNullOrEmpty(cur) ? d : cur + ";" + d);
            Debug.Log($"[GameModule] Scripting define '{d}' added.");
        }

        private static void RemoveDefine(string d)
        {
            string cur = GetDefines();
            SetDefines(string.Join(";", cur.Split(';').Where(x => x.Trim() != d)));
        }

        private static string GetDefines()
        {
#if UNITY_6000_0_OR_NEWER
            var t = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            return PlayerSettings.GetScriptingDefineSymbols(t);
#else
            var g = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(g);
#endif
        }

        private static void SetDefines(string value)
        {
#if UNITY_6000_0_OR_NEWER
            var t = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            PlayerSettings.SetScriptingDefineSymbols(t, value);
#else
            var g = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(g, value);
#endif
        }
    }

    // ========================================================================
    //  EDITOR WINDOW
    // ========================================================================

    public class GameModuleSetup : EditorWindow
    {
        // ── Enums ──────────────────────────────────────────────────────────
        public enum EduBoard  { CBSE, ICSE, StateBoard }
        public enum Grade     { Grade6, Grade7, Grade8, Grade9, Grade10, Grade11, Grade12 }
        public enum Subject   { Physics, Chemistry, Biology, Mathematics }

        [Serializable]
        internal class ModuleConfig
        {
            public string board, grade, subject, topic, createdDate;
        }

        // ── Constants ──────────────────────────────────────────────────────
        private const string PACKAGE_ID             = "com.unity.addressables";
        private const string MODULES_ROOT           = "Assets/Modules";
        private const string GROUP_REMOTE_SCENES    = "RemoteScenes";
        private const string GROUP_DEFAULT_LOCAL     = "Default Local Group";
        private const string GROUP_BUILTIN_DATA      = "Built In Data";
        private const string COMPANY_NAME            = "mhcockpit";
        private const string JSON_CATALOG_DEFINE     = "ENABLE_JSON_CATALOG";

        // Profile variable names — "Remote" preset paths.
        private const string PROFILE_BASE_URL       = "CustomBaseURL";
        private const string PROFILE_REMOTE_BUILD   = "Remote.BuildPath";
        private const string PROFILE_REMOTE_LOAD    = "Remote.LoadPath";

        // Profile variable values.
        private const string VALUE_BASE_URL         = "http://localhost";
        private const string VALUE_REMOTE_BUILD     = "ServerData/[BuildTarget]";
        private const string VALUE_REMOTE_LOAD      = "{CustomBaseURL}/[BuildTarget]";

        // Provider type strings (fixes <none> Asset Provider).
        private const string PROVIDER_ASSET_BUNDLE =
            "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider, " +
            "Unity.ResourceManager";
        private const string PROVIDER_BUNDLED_ASSET =
            "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider, " +
            "Unity.ResourceManager";

        // Scene address constants.
        private const string ADDR_PRACTICE   = "practice";
        private const string ADDR_EVALUATION = "evaluation";

        // Shader variant collection path.
        private const string SVC_PATH = MODULES_ROOT + "/ModuleShaderVariants.shadervariants";

        // Step-8 completion marker.
        private const string SETUP_DONE_MARKER =
            "Assets/AddressableAssetsData/gamemodule_setup.done";

        // ── Package install state ──────────────────────────────────────────
        private static ListRequest s_listReq;
        private static AddRequest  s_addReq;
        private bool _pkgBusy;

        // ── Module config fields ───────────────────────────────────────────
        private EduBoard _board   = EduBoard.CBSE;
        private Grade    _grade   = Grade.Grade12;
        private Subject  _subject = Subject.Physics;
        private string   _topic   = string.Empty;

        // ── Step flags ─────────────────────────────────────────────────────
        private bool _s1, _s2, _s3, _s4, _s5, _s6, _s7, _s8;

        // ── UI state ───────────────────────────────────────────────────────
        private Vector2 _scroll;
        private string  _msg   = string.Empty;
        private bool    _msgOk = true;

        // ── Styles ─────────────────────────────────────────────────────────
        private GUIStyle _titleStyle, _stepStyle, _doneStyle, _pendingStyle;
        private GUIStyle _smallStyle, _okStyle, _warnStyle;
        private bool     _stylesReady;

        private static readonly Color COL_GREEN   = new(0.22f, 0.78f, 0.42f, 1f);
        private static readonly Color COL_ORANGE  = new(0.95f, 0.72f, 0.15f, 1f);
        private static readonly Color COL_DIVIDER = new(0.32f, 0.32f, 0.32f, 1f);

        // URP pass types for shader variant collection.
        private static readonly PassType[] k_URPPassTypes =
        {
            PassType.ScriptableRenderPipeline,
            PassType.ScriptableRenderPipelineDefaultUnlit,
        };

        // ── Menu ───────────────────────────────────────────────────────────
        [MenuItem("Tools/Game Module/Project Setup", false, 0)]
        public static void Open()
        {
            var w = GetWindow<GameModuleSetup>(false, "Game Module — Project Setup", true);
            w.minSize = new Vector2(440, 680);
            w.Show();
        }

        // ================================================================
        #region Lifecycle
        // ================================================================

        private void OnEnable()
        {
            _stylesReady = false;
            _topic = Application.productName;
            RefreshFlags();
        }

        private void Update()
        {
            if (!_pkgBusy) return;
            bool dirty = false;

            if (s_listReq is { IsCompleted: true })
            { OnListDone(); _pkgBusy = false; dirty = true; }
            else if (s_addReq is { IsCompleted: true })
            { OnAddDone(); _pkgBusy = false; dirty = true; }

            if (dirty) Repaint();
        }

        #endregion

        // ================================================================
        #region OnGUI
        // ================================================================

        private void OnGUI()
        {
            BuildStyles();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(10);
            DrawHeader();
            GUILayout.Space(6);

#if !ADDRESSABLES_INSTALLED
            EditorGUILayout.HelpBox(
                "Waiting for ADDRESSABLES_INSTALLED define — Unity is recompiling.\n" +
                "This window will update automatically.",
                MessageType.Warning);
            GUILayout.Space(4);
#endif

            DrawStep(1, "Install Addressables",       _s1, true, DrawBody1);
            DrawStep(2, "Create Module Folder",        _s2, _s1,  DrawBody2);
            DrawStep(3, "Create Addressables Settings", _s3, _s2, DrawBody3);
            DrawStep(4, "Configure Profiles",          _s4, _s3,  DrawBody4);
            DrawStep(5, "Configure RemoteScenes Group", _s5, _s4, DrawBody5);
            DrawStep(6, "Configure Group Schema",      _s6, _s5,  DrawBody6);
            DrawStep(7, "Assign Scene Addresses",      _s7, _s6,  DrawBody7);
            DrawStep(8, "Save & Finish",               _s8, _s7,  DrawBody8);

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

        // ================================================================
        #region Layout Helpers
        // ================================================================

        private void DrawHeader()
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(48));
            EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f));
            GUI.Label(new Rect(r.x, r.y + 5, r.width, 26),
                "Game Module", _titleStyle);
            GUI.Label(new Rect(r.x, r.y + 28, r.width, 16),
                "Addressables Setup Wizard",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 10 });
        }

        private void DrawStep(int n, string title, bool done, bool unlocked, Action body)
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Height(30));
            GUILayout.Space(12);

            var badge = GUILayoutUtility.GetRect(22, 22,
                GUILayout.Width(22), GUILayout.Height(22));
            badge.y += 4;
            EditorGUI.DrawRect(badge,
                done ? COL_GREEN :
                unlocked ? COL_DIVIDER : new Color(0.18f, 0.18f, 0.18f));
            GUI.Label(badge, done ? "✓" : n.ToString(),
                new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize  = 10,
                    normal    = { textColor = done ? Color.black : Color.white }
                });

            GUILayout.Space(8);
            GUILayout.Label(title,
                done ? _doneStyle : unlocked ? _stepStyle : _pendingStyle,
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
                GUILayout.Label($"Complete Step {n - 1} to unlock.", _smallStyle);
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            var div = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(1), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(div, COL_DIVIDER);
        }

        private void StepBtn(int n, bool done, Action run, bool enabled = true)
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
                _msg, _msgOk ? _okStyle : _warnStyle);
        }

        #endregion

        // ================================================================
        #region Step Body Drawers
        // ================================================================

        private void DrawBody1()
        {
            GUILayout.Label("Installs com.unity.addressables if missing.", _smallStyle);
            GUILayout.Space(4);
            StepBtn(1, _s1, RunStep1, !_pkgBusy);
            if (_pkgBusy) GUILayout.Label("Working…", _smallStyle);
        }

        private void DrawBody2()
        {
            _board   = (EduBoard)EditorGUILayout.EnumPopup("Board",   _board);
            _grade   = (Grade)EditorGUILayout.EnumPopup("Grade",     _grade);
            _subject = (Subject)EditorGUILayout.EnumPopup("Subject", _subject);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Topic", _topic);
            EditorGUI.EndDisabledGroup();
            GUILayout.Label(
                "Folder: Assets/Modules/{Board}/Grade{N}/{Subject}/{Topic}/\n" +
                "Scenes: Practice.unity  ·  Evaluation.unity  (flat — no sub-folders)",
                _smallStyle);
            GUILayout.Space(4);
            if (GUILayout.Button("Create Module Folder",
                    GUILayout.Height(26), GUILayout.MaxWidth(200)))
                RunStep2();
        }

        private void DrawBody3()
        {
            if (_s3) GUILayout.Label("✓ Addressables Settings exist.", _doneStyle);
            else     GUILayout.Label("Creates Addressables Settings programmatically.", _smallStyle);
            GUILayout.Space(4);
            StepBtn(3, _s3, RunStep3);
        }

        private void DrawBody4()
        {
            GUILayout.Label(
                $"CustomBaseURL     = {VALUE_BASE_URL}\n" +
                $"Remote.BuildPath  = {VALUE_REMOTE_BUILD}\n" +
                $"Remote.LoadPath   = {VALUE_REMOTE_LOAD}",
                _smallStyle);
            GUILayout.Space(4);
            StepBtn(4, _s4, RunStep4);
        }

        private void DrawBody5()
        {
            GUILayout.Label(
                "Renames 'Default Local Group' → 'RemoteScenes'.\n" +
                "Both Practice and Evaluation scenes go into this single group.",
                _smallStyle);
            GUILayout.Space(4);
            StepBtn(5, _s5, RunStep5);
        }

        private void DrawBody6()
        {
            GUILayout.Label(
                "Remote paths · LZ4 · CRC · Append Hash · Pack Separately\n" +
                "Asset/Bundle providers · JSON catalog · Shader variants\n" +
                "Built In Data → Remote paths · Strict variant matching OFF",
                _smallStyle);
            GUILayout.Space(4);
            StepBtn(6, _s6, RunStep6);
        }

        private void DrawBody7()
        {
            GUILayout.Label(
                "Scans module folder for Practice.unity and Evaluation.unity.\n" +
                "Assigns Addressable addresses:\n" +
                $"  Practice.unity   → \"{ADDR_PRACTICE}\"\n" +
                $"  Evaluation.unity → \"{ADDR_EVALUATION}\"",
                _smallStyle);
            GUILayout.Space(4);
            StepBtn(7, _s7, RunStep7);
        }

        private void DrawBody8()
        {
            GUILayout.Label("Saves all assets and writes completion marker.", _smallStyle);
            GUILayout.Space(4);
            StepBtn(8, _s8, RunStep8);
        }

        #endregion

        // ================================================================
        #region Step Logic
        // ================================================================

        // ── Step 1: Install Addressables ───────────────────────────────────

        private void RunStep1()
        {
            Log("Checking installed packages…", true);
            _pkgBusy  = true;
            s_listReq = Client.List(false, true);
        }

        private void OnListDone()
        {
            if (s_listReq.Status != StatusCode.Success)
            { Log($"Package list error: {s_listReq.Error?.message}", false); return; }

            if (s_listReq.Result.Any(p =>
                    p.name.Equals(PACKAGE_ID, StringComparison.OrdinalIgnoreCase)))
            {
                _s1 = true;
                Log("Addressables already installed. ✓", true);
            }
            else
            {
                Log("Installing com.unity.addressables…", true);
                _pkgBusy = true;
                s_addReq = Client.Add(PACKAGE_ID);
            }
        }

        private void OnAddDone()
        {
            if (s_addReq.Status == StatusCode.Success)
            { _s1 = true; Log("Installed. Reopen window after recompile. ✓", true); }
            else
                Log($"Install failed: {s_addReq.Error?.message}", false);
        }

        // ── Step 2: Create Module Folder ───────────────────────────────────

        private void RunStep2()
        {
            string root  = Path.Combine(Application.dataPath, "Modules");
            string board = Path.Combine(root, _board.ToString());
            string grade = Path.Combine(board, _grade.ToString());
            string subj  = Path.Combine(grade, _subject.ToString());
            string topic = Path.Combine(subj, _topic);

            if (Directory.Exists(topic))
            { _s2 = true; Log("Module folder already exists. ✓", true); return; }

            try
            {
                // Flat structure — scenes live directly inside the topic folder.
                Directory.CreateDirectory(topic);

                File.WriteAllText(Path.Combine(topic, "module_config.json"),
                    JsonUtility.ToJson(new ModuleConfig
                    {
                        board       = _board.ToString(),
                        grade       = _grade.ToString(),
                        subject     = _subject.ToString(),
                        topic       = _topic,
                        createdDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }, true));

                AssetDatabase.Refresh();
                _s2 = true;
                Log("Module folder created. ✓", true);
                Debug.Log($"[GameModule] Folder: {topic}");
            }
            catch (Exception ex)
            {
                Log("Failed to create folders. See Console.", false);
                Debug.LogError($"[GameModule] {ex}");
            }
        }

        // ── Step 3: Create Addressables Settings ───────────────────────────

        private void RunStep3()
        {
#if ADDRESSABLES_INSTALLED
            if (AddressableAssetSettingsDefaultObject.Settings != null)
            { _s3 = true; Log("Settings already exist. ✓", true); return; }

            try
            {
                var settings = AddressableAssetSettings.Create(
                    AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                    AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName,
                    true, true);

                if (settings == null)
                {
                    Log("Failed — AddressableAssetSettings.Create() returned null.", false);
                    return;
                }

                AddressableAssetSettingsDefaultObject.Settings = settings;
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                _s3 = true;
                Log("Addressables Settings created. ✓", true);

                // Safe to open now — settings already on disk, no domain reload.
                EditorApplication.ExecuteMenuItem(
                    "Window/Asset Management/Addressables/Groups");
            }
            catch (Exception ex)
            {
                Log("Exception. See Console.", false);
                Debug.LogError($"[GameModule] Step 3: {ex}");
            }
#else
            WarnNotReady();
#endif
        }

        // ── Step 4: Configure Profiles ─────────────────────────────────────

        private void RunStep4()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;
            var p = s.profileSettings;

            SetProfileVar(p, PROFILE_BASE_URL,     VALUE_BASE_URL);
            SetProfileVar(p, PROFILE_REMOTE_BUILD, VALUE_REMOTE_BUILD);
            SetProfileVar(p, PROFILE_REMOTE_LOAD,  VALUE_REMOTE_LOAD);

            EditorUtility.SetDirty(s);
            _s4 = true;
            Log("Profiles configured. ✓", true);
#else
            WarnNotReady();
#endif
        }

        // ── Step 5: Rename Default Local Group → RemoteScenes ──────────────

        private void RunStep5()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            // Check if RemoteScenes already exists.
            var existing = s.groups.FirstOrDefault(g =>
                g != null && g.Name == GROUP_REMOTE_SCENES);
            if (existing != null)
            {
                _s5 = true;
                Log("RemoteScenes group already exists. ✓", true);
                return;
            }

            // Find the Default Local Group and rename it.
            var defaultGroup = s.groups.FirstOrDefault(g =>
                g != null && g.Name == GROUP_DEFAULT_LOCAL);

            if (defaultGroup != null)
            {
                defaultGroup.Name = GROUP_REMOTE_SCENES;
                EditorUtility.SetDirty(defaultGroup);
                Debug.Log($"[GameModule] Renamed '{GROUP_DEFAULT_LOCAL}' → '{GROUP_REMOTE_SCENES}'.");
            }
            else
            {
                // Default Local Group was already renamed or deleted — create fresh.
                s.CreateGroup(GROUP_REMOTE_SCENES, false, false, true, null,
                    typeof(BundledAssetGroupSchema),
                    typeof(ContentUpdateGroupSchema));
                Debug.Log($"[GameModule] Created '{GROUP_REMOTE_SCENES}' group.");
            }

            // Ensure schemas exist on the group.
            var group = s.groups.FirstOrDefault(g =>
                g != null && g.Name == GROUP_REMOTE_SCENES);
            if (group != null)
            {
                if (group.GetSchema<BundledAssetGroupSchema>() == null)
                    group.AddSchema<BundledAssetGroupSchema>();
                if (group.GetSchema<ContentUpdateGroupSchema>() == null)
                    group.AddSchema<ContentUpdateGroupSchema>();
                EditorUtility.SetDirty(group);
            }

            EditorUtility.SetDirty(s);
            _s5 = true;
            Log("RemoteScenes group ready. ✓", true);
#else
            WarnNotReady();
#endif
        }

        // ── Step 6: Configure Group Schema + Global Settings ───────────────

        private void RunStep6()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            ApplyGlobalSettings(s);

            bool ok = ApplyRemoteScenesSchema(s)
                    & ApplyBuiltInGroupSchema(s);
            if (!ok) return;

            AssetDatabase.SaveAssets();

            PrepareModuleShaders();

            _s6 = true;
            Log("All schema, global, and shader settings applied. ✓", true);
#else
            WarnNotReady();
#endif
        }

        // ── Step 7: Assign Scene Addresses ─────────────────────────────────

        private void RunStep7()
        {
#if ADDRESSABLES_INSTALLED
            var s = GetSettings(); if (s == null) return;

            if (!Directory.Exists(MODULES_ROOT))
            { Log($"'{MODULES_ROOT}' missing. Run Step 2.", false); return; }

            var group = s.groups.FirstOrDefault(g =>
                g != null && g.Name == GROUP_REMOTE_SCENES);
            if (group == null)
            { Log("RemoteScenes group missing. Run Step 5.", false); return; }

            string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { MODULES_ROOT });
            if (guids.Length == 0)
            { Log("No scenes found under Assets/Modules.", false); return; }

            int assigned = 0;
            foreach (string guid in guids)
            {
                string path     = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = Path.GetFileNameWithoutExtension(path);

                string address = null;
                if (fileName.Equals("Practice", StringComparison.OrdinalIgnoreCase))
                    address = ADDR_PRACTICE;
                else if (fileName.Equals("Evaluation", StringComparison.OrdinalIgnoreCase))
                    address = ADDR_EVALUATION;

                if (address == null)
                {
                    Debug.LogWarning($"[GameModule] Skipping unrecognised scene: {path}");
                    continue;
                }

                var entry = s.CreateOrMoveEntry(guid, group, false, false);
                if (entry == null) continue;

                entry.address = address;
                assigned++;
                Debug.Log($"[GameModule] {fileName}.unity → address: \"{address}\"");
            }

            if (assigned == 0)
            {
                Log("No Practice.unity or Evaluation.unity found. " +
                    "Place scenes in the module folder.", false);
                return;
            }

            s.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            EditorUtility.SetDirty(s);
            _s7 = true;
            Log($"{assigned} scene(s) assigned to RemoteScenes. ✓", true);
#else
            WarnNotReady();
#endif
        }

        // ── Step 8: Save & Finish ──────────────────────────────────────────

        private void RunStep8()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string markerAbs = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SETUP_DONE_MARKER));
            string dir = Path.GetDirectoryName(markerAbs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(markerAbs, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AssetDatabase.ImportAsset(SETUP_DONE_MARKER);

            _s8 = true;
            Log("Setup complete! All assets saved. ✓", true);
            Debug.Log("[GameModule] Setup finished.");
        }

        #endregion

        // ================================================================
        #region Global + Schema Configuration
        // ================================================================

#if ADDRESSABLES_INSTALLED
        private static void ApplyGlobalSettings(AddressableAssetSettings s)
        {
            s.OverridePlayerVersion = COMPANY_NAME;
            s.BuildRemoteCatalog    = true;
            s.EnableJsonCatalog     = true;
            s.ContiguousBundles     = true;
            s.NonRecursiveBuilding  = true;
            EnsureScriptingDefine(JSON_CATALOG_DEFINE);

            // Global catalog build/load paths → Remote.
            s.RemoteCatalogBuildPath.SetVariableByName(s, PROFILE_REMOTE_BUILD);
            s.RemoteCatalogLoadPath.SetVariableByName(s, PROFILE_REMOTE_LOAD);

            var so = new SerializedObject(s);
            so.Update();
            SetBool(so, true, "logRuntimeExceptions",
                "m_BuildSettings.m_LogResourceManagerExceptions");
            SetInt(so, "m_InternalIdNamingMode",   0); // Full Path
            SetInt(so, "m_InternalBundleIdMode",   2); // Group Guid Project Id Hash
            SetInt(so, "m_MonoScriptBundleNaming", 1); // Project Name Hash
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(s);
            Debug.Log("[GameModule] Global settings applied — catalog → Remote, JSON enabled.");
        }

        private static bool ApplyRemoteScenesSchema(AddressableAssetSettings s)
        {
            var group = s.groups.FirstOrDefault(g =>
                g != null && g.Name == GROUP_REMOTE_SCENES);
            if (group == null)
            {
                Debug.LogError($"[GameModule] '{GROUP_REMOTE_SCENES}' not found. Run Step 5.");
                return false;
            }

            var b = group.GetSchema<BundledAssetGroupSchema>()
                 ?? group.AddSchema<BundledAssetGroupSchema>();

            b.BuildPath.SetVariableByName(s, PROFILE_REMOTE_BUILD);
            b.LoadPath.SetVariableByName(s, PROFILE_REMOTE_LOAD);

            b.Compression              = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            b.UseAssetBundleCrc        = true;
            b.BundleNaming             = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
            b.UseAssetBundleCache      = true;
            b.BundleMode               = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            b.IncludeAddressInCatalog   = true;
            b.IncludeGUIDInCatalog      = false;

            var bso = new SerializedObject(b);
            bso.Update();
            SetManagedType(bso, "m_BundledAssetProviderType", PROVIDER_BUNDLED_ASSET);
            SetManagedType(bso, "m_AssetBundleProviderType",  PROVIDER_ASSET_BUNDLE);
            SetBoolSingle(bso, "m_IncludeLabelsInCatalog", false);
            bso.ApplyModifiedPropertiesWithoutUndo();

            var cu = group.GetSchema<ContentUpdateGroupSchema>()
                  ?? group.AddSchema<ContentUpdateGroupSchema>();
            cu.StaticContent = false;

            EditorUtility.SetDirty(group);
            Debug.Log($"[GameModule] Schema applied to '{GROUP_REMOTE_SCENES}' → Remote paths.");
            return true;
        }

        private static bool ApplyBuiltInGroupSchema(AddressableAssetSettings s)
        {
            var group =
                s.groups.FirstOrDefault(g => g != null && g.Name == GROUP_BUILTIN_DATA)
             ?? s.groups.FirstOrDefault(g => g != null && g.Name == GROUP_DEFAULT_LOCAL);

            if (group == null)
            {
                Debug.LogWarning("[GameModule] No Built In Data / Default Local Group found. Skipping.");
                return true;
            }

            var b = group.GetSchema<BundledAssetGroupSchema>()
                 ?? group.AddSchema<BundledAssetGroupSchema>();

            b.BuildPath.SetVariableByName(s, PROFILE_REMOTE_BUILD);
            b.LoadPath.SetVariableByName(s, PROFILE_REMOTE_LOAD);
            b.Compression         = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            b.BundleNaming        = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
            b.UseAssetBundleCache = true;
            b.BundleMode          = BundledAssetGroupSchema.BundlePackingMode.PackTogether;

            var bso = new SerializedObject(b);
            bso.Update();
            SetManagedType(bso, "m_BundledAssetProviderType", PROVIDER_BUNDLED_ASSET);
            SetManagedType(bso, "m_AssetBundleProviderType",  PROVIDER_ASSET_BUNDLE);
            bso.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(group);
            Debug.Log($"[GameModule] Built-in group '{group.Name}' → Remote paths.");
            return true;
        }

        private static void SetProfileVar(
            AddressableAssetProfileSettings p, string name, string value)
        {
            if (!p.GetVariableNames().Contains(name))
            {
                p.CreateValue(name, value);
                Debug.Log($"[GameModule] Profile var created: {name} = {value}");
                return;
            }
            string id = p.GetProfileId("Default");
            if (!string.IsNullOrEmpty(id))
            {
                p.SetValue(id, name, value);
                Debug.Log($"[GameModule] Profile var updated: {name} = {value}");
            }
        }
#endif

        #endregion

        // ================================================================
        #region Shader Variant Preparation  (public for BuildAndUpload)
        // ================================================================

#if ADDRESSABLES_INSTALLED
        /// <summary>
        /// Scans all materials under Assets/Modules, builds a
        /// ShaderVariantCollection covering URP pass types, registers it in
        /// PlayerSettings.preloadedAssets, and adds shaders to
        /// GraphicsSettings.alwaysIncludedShaders.
        ///
        /// Called from Step 6 and from the build pipeline before building.
        /// </summary>
        internal static void PrepareModuleShaders()
        {
            string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { MODULES_ROOT });
            if (matGuids.Length == 0)
            {
                Debug.Log("[GameModule] No materials under Assets/Modules — shader prep skipped.");
                return;
            }

            var shaders = new List<Shader>();
            var svc     = new ShaderVariantCollection();

            foreach (string guid in matGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;

                if (!shaders.Contains(mat.shader))
                    shaders.Add(mat.shader);

                // URP pass types.
                foreach (var pt in k_URPPassTypes)
                {
                    try { svc.Add(new ShaderVariantCollection.ShaderVariant(mat.shader, pt)); }
                    catch { /* pass not declared */ }

                    if (mat.shaderKeywords is { Length: > 0 })
                    {
                        try { svc.Add(new ShaderVariantCollection.ShaderVariant(
                                  mat.shader, pt, mat.shaderKeywords)); }
                        catch { /* invalid combo */ }
                    }
                }

                // Fallback: PassType.Normal for non-URP / custom shaders.
                try { svc.Add(new ShaderVariantCollection.ShaderVariant(
                          mat.shader, PassType.Normal)); }
                catch { }

                if (mat.shaderKeywords is { Length: > 0 })
                {
                    try { svc.Add(new ShaderVariantCollection.ShaderVariant(
                              mat.shader, PassType.Normal, mat.shaderKeywords)); }
                    catch { }
                }
            }

            // 1. Persist the SVC asset.
            var existing = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(SVC_PATH);
            if (existing != null)
            {
                EditorUtility.CopySerialized(svc, existing);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
            }
            else
            {
                string dir = Path.GetDirectoryName(SVC_PATH);
                if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                    Directory.CreateDirectory(
                        Path.Combine(Application.dataPath, "..", dir));
                AssetDatabase.CreateAsset(svc, SVC_PATH);
            }
            AssetDatabase.Refresh();

            // 2. Register in PlayerSettings.preloadedAssets.
            //    This is the CORRECT way to affect Addressables builds.
            //    Do NOT add the SVC as an Addressable entry — PackSeparately
            //    would isolate it in its own bundle where it has zero effect.
            var svcAsset = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(SVC_PATH);
            if (svcAsset != null)
            {
                var preloaded = PlayerSettings.GetPreloadedAssets()
                             ?? Array.Empty<UnityEngine.Object>();
                if (!Array.Exists(preloaded, a => a == svcAsset))
                {
                    var updated = new UnityEngine.Object[preloaded.Length + 1];
                    preloaded.CopyTo(updated, 0);
                    updated[preloaded.Length] = svcAsset;
                    PlayerSettings.SetPreloadedAssets(updated);
                    Debug.Log("[GameModule] SVC added to PlayerSettings.preloadedAssets.");
                }
            }

            // 3. Always Included Shaders.
            AddShadersToAlwaysIncluded(shaders);

            // 4. Disable strict shader variant matching (Unity 6 default).
#if UNITY_6000_0_OR_NEWER
            if (PlayerSettings.strictShaderVariantMatching)
            {
                PlayerSettings.strictShaderVariantMatching = false;
                Debug.Log("[GameModule] Disabled strictShaderVariantMatching.");
            }
#endif

            Debug.Log($"[GameModule] Shader prep done — {shaders.Count} shader(s), " +
                      $"{svc.shaderCount} variant entry/entries.");
        }

        private static void AddShadersToAlwaysIncluded(List<Shader> shaders)
        {
            var gsAssets = AssetDatabase.LoadAllAssetsAtPath(
                "ProjectSettings/GraphicsSettings.asset");
            if (gsAssets == null || gsAssets.Length == 0) return;

            var gso = new SerializedObject(gsAssets[0]);
            gso.Update();

            var prop = gso.FindProperty("m_AlwaysIncludedShaders");
            if (prop == null) return;

            var ids = new HashSet<int>();
            for (int i = 0; i < prop.arraySize; i++)
            {
                var obj = prop.GetArrayElementAtIndex(i).objectReferenceValue;
                if (obj != null) ids.Add(obj.GetInstanceID());
            }

            int added = 0;
            foreach (var shader in shaders)
            {
                if (shader == null || ids.Contains(shader.GetInstanceID())) continue;
                prop.InsertArrayElementAtIndex(prop.arraySize);
                prop.GetArrayElementAtIndex(prop.arraySize - 1).objectReferenceValue = shader;
                ids.Add(shader.GetInstanceID());
                added++;
            }

            if (added > 0)
            {
                gso.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"[GameModule] Added {added} shader(s) to Always Included Shaders.");
            }
        }
#endif

        #endregion

        // ================================================================
        #region SerializedObject Helpers
        // ================================================================

        private static void SetBoolSingle(SerializedObject so, string field, bool v)
        {
            var p = so.FindProperty(field);
            if (p != null) p.boolValue = v;
        }

        private static void SetBool(SerializedObject so, bool v, params string[] names)
        {
            foreach (string n in names)
            {
                var p = so.FindProperty(n);
                if (p != null) { p.boolValue = v; return; }
            }
        }

        private static void SetInt(SerializedObject so, string field, int v)
        {
            var p = so.FindProperty(field);
            if (p != null) p.intValue = v;
        }

        private static void SetManagedType(SerializedObject so, string field, string aqn)
        {
            var tp = so.FindProperty(field);
            if (tp == null) return;

            var child = tp.FindPropertyRelative("m_AssemblyQualifiedName");
            if (child != null) { child.stringValue = aqn; return; }

            var cls = tp.FindPropertyRelative("m_ClassName");
            var asm = tp.FindPropertyRelative("m_AssemblyName");
            if (cls != null && asm != null)
            {
                int comma = aqn.IndexOf(',');
                if (comma > 0)
                {
                    cls.stringValue = aqn.Substring(0, comma).Trim();
                    asm.stringValue = aqn.Substring(comma + 1).Trim();
                }
                else
                {
                    cls.stringValue = aqn.Trim();
                    asm.stringValue = string.Empty;
                }
                return;
            }

            // Last-resort: walk children looking for a "type" string property.
            var it = tp.Copy();
            bool nxt = it.Next(true);
            while (nxt)
            {
                if (it.propertyType == SerializedPropertyType.String &&
                    it.name.ToLower().Contains("type"))
                { it.stringValue = aqn; return; }
                nxt = it.Next(false);
            }
        }

        private static bool EnsureScriptingDefine(string define)
        {
#if UNITY_6000_0_OR_NEWER
            var t = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            string cur = PlayerSettings.GetScriptingDefineSymbols(t);
            if (cur.Split(';').Any(x => x.Trim() == define)) return false;
            PlayerSettings.SetScriptingDefineSymbols(t,
                string.IsNullOrEmpty(cur) ? define : cur + ";" + define);
            return true;
#else
            var g = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            string cur = PlayerSettings.GetScriptingDefineSymbolsForGroup(g);
            if (cur.Split(';').Any(x => x.Trim() == define)) return false;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(g,
                string.IsNullOrEmpty(cur) ? define : cur + ";" + define);
            return true;
#endif
        }

        #endregion

        // ================================================================
        #region Refresh Flags
        // ================================================================

        private void RefreshFlags()
        {
#if ADDRESSABLES_INSTALLED
            _s1 = true;
            _s2 = Directory.Exists(Path.Combine(Application.dataPath, "Modules"));

            var s = AddressableAssetSettingsDefaultObject.Settings;
            _s3 = s != null;

            if (s != null)
            {
                var pn = s.profileSettings.GetVariableNames();
                _s4 = pn.Contains(PROFILE_BASE_URL)
                    && pn.Contains(PROFILE_REMOTE_BUILD)
                    && pn.Contains(PROFILE_REMOTE_LOAD);

                _s5 = s.groups.Any(g => g != null && g.Name == GROUP_REMOTE_SCENES);

                _s6 = _s5 && IsGroupConfigured(s, GROUP_REMOTE_SCENES);

                _s7 = _s6 && HasSceneEntries(s);

                _s8 = _s7 && File.Exists(
                    Path.GetFullPath(
                        Path.Combine(Application.dataPath, "..", SETUP_DONE_MARKER)));
            }
#else
            _s1 = false;
            _s2 = Directory.Exists(Path.Combine(Application.dataPath, "Modules"));
#endif
        }

#if ADDRESSABLES_INSTALLED
        private static bool IsGroupConfigured(AddressableAssetSettings s, string name)
        {
            var g = s.groups.FirstOrDefault(x => x != null && x.Name == name);
            if (g == null) return false;
            var schema = g.GetSchema<BundledAssetGroupSchema>();
            return schema != null
                && schema.BuildPath.GetName(s) == PROFILE_REMOTE_BUILD;
        }

        private static bool HasSceneEntries(AddressableAssetSettings s)
        {
            var g = s.groups.FirstOrDefault(x => x != null && x.Name == GROUP_REMOTE_SCENES);
            return g != null && g.entries.Count > 0;
        }
#endif

        #endregion

        // ================================================================
        #region Utilities
        // ================================================================

        private void Log(string msg, bool ok)
        { _msg = msg; _msgOk = ok; Repaint(); }

        private void WarnNotReady() =>
            Log("Addressables not ready — close and reopen if Step 1 is done.", false);

#if ADDRESSABLES_INSTALLED
        private AddressableAssetSettings GetSettings()
        {
            var s = AddressableAssetSettingsDefaultObject.Settings;
            if (s == null) Log("Settings missing. Run Step 3.", false);
            return s;
        }
#endif

        #endregion

        // ================================================================
        #region Styles
        // ================================================================

        private void BuildStyles()
        {
            if (_stylesReady) return;
            _titleStyle   = new GUIStyle(EditorStyles.boldLabel)
                { fontSize = 16, alignment = TextAnchor.MiddleCenter,
                  normal = { textColor = Color.white } };
            _stepStyle    = new GUIStyle(EditorStyles.label)
                { fontSize = 12, normal = { textColor = new Color(0.88f, 0.88f, 0.88f) } };
            _doneStyle    = new GUIStyle(_stepStyle)
                { normal = { textColor = COL_GREEN } };
            _pendingStyle = new GUIStyle(_stepStyle)
                { normal = { textColor = new Color(0.45f, 0.45f, 0.45f) } };
            _smallStyle   = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                { normal = { textColor = new Color(0.60f, 0.60f, 0.60f) } };
            _okStyle      = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = COL_GREEN }, wordWrap = true };
            _warnStyle    = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = COL_ORANGE }, wordWrap = true };
            _stylesReady  = true;
        }

        #endregion
    }

    // ========================================================================
    //  SHADER PREPROCESSOR — prevents URP from stripping module variants
    // ========================================================================

    /// <summary>
    /// IPreprocessShaders callback that runs BEFORE URP's own stripper
    /// (callbackOrder = -100 vs URP's 0). For any shader referenced by a
    /// material under Assets/Modules, ALL variants are preserved — preventing
    /// the pink-material problem when bundles load in the Dashboard project.
    /// </summary>
    internal class ModuleShaderPreprocessor : IPreprocessShaders
    {
        private static HashSet<Shader> s_cache;
        public int callbackOrder => -100;

        private static HashSet<Shader> GetModuleShaders()
        {
            if (s_cache != null) return s_cache;
            s_cache = new HashSet<Shader>();

            string[] guids = AssetDatabase.FindAssets("t:Material",
                new[] { "Assets/Modules" });
            foreach (string guid in guids)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (mat?.shader != null)
                    s_cache.Add(mat.shader);
            }

            Debug.Log($"[GameModule] ShaderPreprocessor — " +
                      $"protecting {s_cache.Count} shader(s).");
            return s_cache;
        }

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            // Return without modifying `data` → zero stripping.
            if (GetModuleShaders().Contains(shader)) return;
        }
    }
}

#endif