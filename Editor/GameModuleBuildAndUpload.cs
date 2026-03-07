// ============================================================================
//  GameModuleBuildAndUpload.cs
//  Menu: Tools → Game Module → Build And Upload
//
//  One-click pipeline:
//    1. Read module_config.json for S3 path derivation
//    2. Switch to WebGL if needed
//    3. Clean previous Addressables output
//    4. Prepare shader variants (prevents pink materials)
//    5. Build Addressables
//    6. Validate JSON catalog exists
//    7. Upload all output files to S3
//
//  Target:  Unity 6.3 LTS+  ·  Addressables 2.8.1+  ·  URP 17.3.0+
// ============================================================================

#if UNITY_EDITOR && ADDRESSABLES_INSTALLED

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Transfer;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace MHCockpit.VLPipe.Editor
{
    public static class GameModuleBuildAndUpload
    {
        // ═══════════════════════════════════════════════════════════════════
        //  S3 CONFIGURATION
        //
        //  Prefer environment variables over hard-coded keys:
        //    export AWS_ACCESS_KEY_ID=...
        //    export AWS_SECRET_ACCESS_KEY=...
        //  Fallback constants are used ONLY when env vars are absent.
        // ═══════════════════════════════════════════════════════════════════

        private const string S3_BUCKET           = "mhc-embibe-test";
        private const string AWS_REGION          = "ap-south-1";
        private const string FALLBACK_ACCESS_KEY = "AKIA5CBGTAUWLZ2GVA4X";
        private const string FALLBACK_SECRET_KEY = "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";

        private static string AwsAccessKey =>
            Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? FALLBACK_ACCESS_KEY;
        private static string AwsSecretKey =>
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? FALLBACK_SECRET_KEY;

        // ═══════════════════════════════════════════════════════════════════
        //  PATH / BUILD CONSTANTS
        // ═══════════════════════════════════════════════════════════════════

        private const string BUILD_ROOT          = "ServerData";
        private const string MODULES_ROOT        = "Assets/Modules";
        private const string CONFIG_FILENAME     = "module_config.json";
        private const string MONOSCRIPTS_FRAG    = "monoscripts";
        private const string BUILTIN_FRAG        = "unitybuiltinassets";
        private const string JSON_CATALOG_DEFINE = "ENABLE_JSON_CATALOG";

        [Serializable]
        private class ModuleConfig
        {
            public string board, grade, subject, topic, createdDate;
        }

        private readonly struct UploadFile
        {
            public readonly string LocalPath;
            public readonly string RelativePath;
            public UploadFile(string local, string relative)
            { LocalPath = local; RelativePath = relative; }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  MENU ITEMS
        // ═══════════════════════════════════════════════════════════════════

        [MenuItem("Tools/Game Module/Build And Upload", false, 1)]
        public static void Trigger() => _ = RunPipelineAsync();

        [MenuItem("Tools/Game Module/Build And Upload", validate = true)]
        public static bool Validate() =>
            !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        // ═══════════════════════════════════════════════════════════════════
        //  PIPELINE
        // ═══════════════════════════════════════════════════════════════════

        private static async Task RunPipelineAsync()
        {
            Debug.Log("[Build] ═══════════════════════════════════════════");
            Debug.Log("[Build]  Game Module — Build & Upload to S3");
            Debug.Log("[Build] ═══════════════════════════════════════════");

            try
            {
                // 1. Module metadata.
                ShowProgress("Reading module config…", 0.02f);
                var config = ReadModuleConfig();
                if (config == null) { Clear(); return; }

                // 2. Platform check.
                ShowProgress("Checking platform…", 0.05f);
                if (!EnsureWebGL()) { Clear(); return; }

                // 3. Build Addressables.
                ShowProgress("Building Addressables…", 0.10f);
                string outputFolder = ExecuteBuild();
                if (outputFolder == null) { Clear(); return; }

                // 4. Upload to S3.
                string target   = EditorUserBuildSettings.activeBuildTarget.ToString();
                string s3Prefix = BuildS3Prefix(config, target);
                Debug.Log($"[Build] Destination: s3://{S3_BUCKET}/{s3Prefix}");
                await UploadAsync(outputFolder, s3Prefix);
            }
            catch (AmazonS3Exception s3Ex)
            {
                Debug.LogError($"[Build] S3 error ({s3Ex.ErrorCode}): {s3Ex.Message}");
                EditorUtility.DisplayDialog("S3 Error",
                    $"{s3Ex.ErrorCode}: {s3Ex.Message}", "OK");
            }
            catch (AmazonClientException clientEx)
            {
                Debug.LogError($"[Build] AWS client error: {clientEx.Message}");
                EditorUtility.DisplayDialog("AWS Error", clientEx.Message, "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Build] Error: {ex}");
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
            finally { Clear(); }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  1. READ MODULE CONFIG
        // ═══════════════════════════════════════════════════════════════════

        private static ModuleConfig ReadModuleConfig()
        {
            string abs = Path.Combine(Application.dataPath, "Modules");
            if (!Directory.Exists(abs))
            {
                Debug.LogError($"[Build] '{MODULES_ROOT}' not found. Run Setup Step 2.");
                return null;
            }

            string[] files = Directory.GetFiles(abs, CONFIG_FILENAME, SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Debug.LogError($"[Build] No '{CONFIG_FILENAME}' found. Run Setup Step 2.");
                return null;
            }
            if (files.Length > 1)
                Debug.LogWarning($"[Build] {files.Length} configs found — using: {files[0]}");

            var cfg = JsonUtility.FromJson<ModuleConfig>(File.ReadAllText(files[0]));
            if (cfg == null
                || string.IsNullOrEmpty(cfg.board)
                || string.IsNullOrEmpty(cfg.grade)
                || string.IsNullOrEmpty(cfg.subject)
                || string.IsNullOrEmpty(cfg.topic))
            {
                Debug.LogError("[Build] module_config.json is corrupt. Re-run Setup Step 2.");
                return null;
            }

            Debug.Log($"[Build] Module: {cfg.board}/{cfg.grade}/{cfg.subject}/{cfg.topic}");
            return cfg;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  2. ENSURE WEBGL
        // ═══════════════════════════════════════════════════════════════════

        private static bool EnsureWebGL()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
            {
                Debug.Log("[Build] Platform: WebGL ✓");
                return true;
            }

            string cur = EditorUserBuildSettings.activeBuildTarget.ToString();
            bool ok = EditorUtility.DisplayDialog(
                "Switch to WebGL?",
                $"Current platform: {cur}\n\n" +
                "Addressables must target WebGL.\nSwitch now?",
                "Switch", "Cancel");

            if (!ok)
            {
                Debug.LogWarning("[Build] Platform switch cancelled.");
                return false;
            }

            ShowProgress("Switching to WebGL…", 0.07f);
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.WebGL, BuildTarget.WebGL);

            if (!switched)
            {
                Debug.LogError("[Build] Platform switch failed. " +
                               "Ensure WebGL Build Support is installed via Unity Hub.");
                return false;
            }

            Debug.Log("[Build] Switched to WebGL. ✓");
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  3. ADDRESSABLES BUILD
        // ═══════════════════════════════════════════════════════════════════

        private static string ExecuteBuild()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[Build] Addressables Settings missing. Run Setup first.");
                return null;
            }

            // Enforce remote JSON catalog every build.
            bool dirty = false;
            if (!settings.BuildRemoteCatalog) { settings.BuildRemoteCatalog = true; dirty = true; }
            if (!settings.EnableJsonCatalog)  { settings.EnableJsonCatalog = true;  dirty = true; }
            bool defineAdded = EnsureDefine(JSON_CATALOG_DEFINE);
            if (dirty) { EditorUtility.SetDirty(settings); AssetDatabase.SaveAssets(); }
            if (defineAdded)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.LogWarning("[Build] Added ENABLE_JSON_CATALOG define. " +
                                 "Wait for recompile, then re-run Build And Upload.");
                EditorUtility.DisplayDialog("Recompile Required",
                    "Added ENABLE_JSON_CATALOG define.\n\n" +
                    "Wait for compilation, then run:\n" +
                    "Tools > Game Module > Build And Upload", "OK");
                return null;
            }
            if (dirty) AssetDatabase.Refresh();

            // ── Clean old build ───────────────────────────────────────────
            ShowProgress("Cleaning previous build…", 0.12f);
            Debug.Log("[Build] Cleaning previous output…");
            AddressableAssetSettings.CleanPlayerContent(settings.ActivePlayerDataBuilder);

            // Also nuke the ServerData folder to guarantee no stale files.
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string serverData  = Path.Combine(projectRoot, BUILD_ROOT);
            if (Directory.Exists(serverData))
            {
                Directory.Delete(serverData, true);
                Debug.Log("[Build] Deleted ServerData/ for clean build.");
            }

            // ── Shader preparation ────────────────────────────────────────
            ShowProgress("Preparing shader variants…", 0.15f);
            Debug.Log("[Build] Preparing shader variants…");
            GameModuleSetup.PrepareModuleShaders();

            // ── Build ─────────────────────────────────────────────────────
            ShowProgress("Building Addressables — may take several minutes…", 0.18f);
            Debug.Log("[Build] Building Addressables…");
            AddressableAssetSettings.BuildPlayerContent(
                out AddressablesPlayerBuildResult result);

            if (!string.IsNullOrEmpty(result.Error))
            {
                Debug.LogError($"[Build] Build failed:\n{result.Error}");
                EditorUtility.DisplayDialog("Build Failed", result.Error, "OK");
                return null;
            }

            Debug.Log($"[Build] Build completed in {result.Duration:F2}s. ✓");
            Debug.Log("[Build] Dashboard reminder:\n" +
                      "  1. PlayerSettings.strictShaderVariantMatching = false (Unity 6+)\n" +
                      "  2. Call Shader.WarmupAllShaders() after Addressables.LoadSceneAsync()");

            // ── Validate output ───────────────────────────────────────────
            string buildTarget  = EditorUserBuildSettings.activeBuildTarget.ToString();
            string outputFolder = Path.Combine(projectRoot, BUILD_ROOT, buildTarget);

            if (!Directory.Exists(outputFolder))
            {
                Debug.LogError($"[Build] Output not found: {outputFolder}\n" +
                               "Check Remote.BuildPath in Addressables profile.");
                return null;
            }

            // JSON catalog validation.
            string[] jsonCatalogs = Directory.GetFiles(
                outputFolder, "catalog*.json", SearchOption.AllDirectories);

            if (jsonCatalogs.Length == 0)
            {
                string[] binCatalogs = Directory.GetFiles(
                    outputFolder, "catalog*.bin", SearchOption.AllDirectories);
                if (binCatalogs.Length > 0)
                {
                    Debug.LogError("[Build] Binary catalog produced instead of JSON.\n" +
                                   "Run Setup Step 6 and ensure Enable Json Catalog is ON.");
                    return null;
                }
                Debug.LogError("[Build] No catalog file found in output.");
                return null;
            }

            Debug.Log($"[Build] Catalog: {jsonCatalogs[0]}");
            int count = Directory.GetFiles(outputFolder, "*", SearchOption.AllDirectories).Length;
            Debug.Log($"[Build] Output: {outputFolder}  ({count} files)");
            ShowProgress($"Build done — {count} file(s) ready.", 0.28f);
            return outputFolder;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  4. S3 UPLOAD
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// S3 prefix uses PascalCase topic to match the convention expected by
        /// the Dashboard when deriving catalog URLs.
        ///
        /// Example: Modules/CBSE/Grade12/Physics/SimplePendulum/WebGL/
        /// </summary>
        private static string BuildS3Prefix(ModuleConfig cfg, string buildTarget) =>
            $"Modules/{cfg.board}/{cfg.grade}/{cfg.subject}/" +
            $"{ToPascalCase(cfg.topic)}/{buildTarget}/";

        private static string ToPascalCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            string[] words = input.Split(
                new[] { ' ', '-', '_', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder(input.Length);
            foreach (string word in words)
            {
                var clean = new System.Text.StringBuilder(word.Length);
                foreach (char c in word)
                    if (char.IsLetterOrDigit(c)) clean.Append(c);
                if (clean.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(clean[0]));
                if (clean.Length > 1) sb.Append(clean.ToString(1, clean.Length - 1));
            }
            return sb.ToString();
        }

        private static async Task UploadAsync(string localFolder, string s3Prefix)
        {
            var files = BuildManifest(localFolder);
            if (files.Count == 0)
            {
                Debug.LogWarning("[Build] Nothing to upload.");
                return;
            }

            Debug.Log($"[Build] Uploading {files.Count} file(s) → s3://{S3_BUCKET}/{s3Prefix}");

            var creds = new BasicAWSCredentials(AwsAccessKey, AwsSecretKey);
            using var client   = new AmazonS3Client(creds, RegionEndpoint.GetBySystemName(AWS_REGION));
            using var transfer = new TransferUtility(client);

            var ok     = new List<string>();
            var failed = new List<string>();

            const float START = 0.30f, RANGE = 0.70f;
            float slice = RANGE / Math.Max(files.Count, 1);

            for (int i = 0; i < files.Count; i++)
            {
                var item     = files[i];
                string name  = Path.GetFileName(item.LocalPath);
                string s3Key = s3Prefix + item.RelativePath;
                long   size  = new FileInfo(item.LocalPath).Length;
                float  baseP = START + i * slice;

                ShowProgress($"Uploading [{i + 1}/{files.Count}] {name}", baseP,
                    $"s3://{S3_BUCKET}/{s3Key}  ({Fmt(size)})");

                try
                {
                    long lastR = 0, every = Math.Max(1, size / 20);
                    var req = new TransferUtilityUploadRequest
                    {
                        BucketName  = S3_BUCKET,
                        FilePath    = item.LocalPath,
                        Key         = s3Key,
                        ContentType = ContentType(item.LocalPath)
                    };

                    int idx = i; // capture for lambda
                    req.UploadProgressEvent += (_, args) =>
                    {
                        if (args.TransferredBytes - lastR < every) return;
                        lastR = args.TransferredBytes;
                        float within  = size > 0 ? (float)args.TransferredBytes / size : 1f;
                        float overall = START + idx * slice + within * slice;
                        ShowProgress(
                            $"Uploading [{idx + 1}/{files.Count}] {name} {args.PercentDone}%",
                            overall,
                            $"{Fmt(args.TransferredBytes)}/{Fmt(size)} → s3://{S3_BUCKET}/{s3Key}");
                    };

                    await transfer.UploadAsync(req);
                    ok.Add(name);
                    Debug.Log($"[Build]   ✓ {name} [{Fmt(size)}]");
                }
                catch (AmazonS3Exception s3Ex)
                {
                    failed.Add(name);
                    Debug.LogError($"[Build]   ✗ {name} — {s3Ex.ErrorCode}: {s3Ex.Message}");
                }
                catch (Exception ex)
                {
                    failed.Add(name);
                    Debug.LogError($"[Build]   ✗ {name} — {ex.Message}");
                }
            }

            // Summary.
            if (failed.Count == 0)
            {
                ShowProgress("Upload complete! ✓", 1f,
                    $"{ok.Count} file(s) → s3://{S3_BUCKET}/{s3Prefix}");
                Debug.Log($"[Build] ✓ {ok.Count} file(s) uploaded.");
                await Task.Delay(1200);
                EditorUtility.DisplayDialog("Upload Complete",
                    $"{ok.Count} file(s) uploaded.\n\nBucket: {S3_BUCKET}\nPrefix: {s3Prefix}",
                    "OK");
            }
            else
            {
                ShowProgress($"Done with {failed.Count} error(s).", 1f);
                Debug.LogWarning($"[Build] {ok.Count} succeeded, {failed.Count} failed: " +
                                 string.Join(", ", failed));
                await Task.Delay(1200);
                EditorUtility.DisplayDialog("Upload Errors",
                    $"Succeeded: {ok.Count}\nFailed: {failed.Count}\n\n" +
                    string.Join("\n", failed), "OK");
            }
        }

        private static List<UploadFile> BuildManifest(string localFolder)
        {
            var list = new List<UploadFile>();
            if (!Directory.Exists(localFolder)) return list;

            string root = localFolder.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (string f in Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length + 1).Replace('\\', '/');
                list.Add(new UploadFile(f, rel));
            }

            AppendFallbackBundles(list);
            return list;
        }

        private static void AppendFallbackBundles(List<UploadFile> manifest)
        {
            bool hasMono = manifest.Any(f =>
                Path.GetFileName(f.LocalPath)
                    .IndexOf(MONOSCRIPTS_FRAG, StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasBuiltin = manifest.Any(f =>
                Path.GetFileName(f.LocalPath)
                    .IndexOf(BUILTIN_FRAG, StringComparison.OrdinalIgnoreCase) >= 0);

            if (hasMono && hasBuiltin) return;

            string fallback = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "com.unity.addressables", "aa",
                EditorUserBuildSettings.activeBuildTarget.ToString());

            if (!Directory.Exists(fallback))
            {
                Debug.LogWarning($"[Build] Fallback bundle dir not found: {fallback}");
                return;
            }

            foreach (string b in Directory.GetFiles(fallback, "*.bundle", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(b);
                bool isMono    = name.IndexOf(MONOSCRIPTS_FRAG, StringComparison.OrdinalIgnoreCase) >= 0;
                bool isBuiltin = name.IndexOf(BUILTIN_FRAG, StringComparison.OrdinalIgnoreCase) >= 0;
                bool needed    = (!hasMono && isMono) || (!hasBuiltin && isBuiltin);
                if (!needed) continue;
                if (manifest.Any(f =>
                        Path.GetFileName(f.LocalPath)
                            .Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                manifest.Add(new UploadFile(b, name));
                Debug.Log($"[Build] Added fallback bundle: {name}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UTILITIES
        // ═══════════════════════════════════════════════════════════════════

        private static void ShowProgress(string title, float p, string info = "")
        {
            string body = string.IsNullOrEmpty(info) ? title : $"{title}\n{info}";
            EditorUtility.DisplayProgressBar("Game Module — Build & Upload", body, Mathf.Clamp01(p));
        }

        private static void Clear() => EditorUtility.ClearProgressBar();

        private static string ContentType(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".json"     => "application/json",
                ".hash"     => "text/plain",
                ".bundle"   => "application/octet-stream",
                ".data"     => "application/octet-stream",
                ".js"       => "application/javascript",
                ".wasm"     => "application/wasm",
                ".unityweb" => "application/octet-stream",
                ".br"       => "application/x-brotli",
                ".gz"       => "application/gzip",
                ".xml"      => "application/xml",
                _           => "application/octet-stream"
            };

        private static string Fmt(long bytes)
        {
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024)     return $"{bytes / 1_024.0:F1} KB";
            return $"{bytes} B";
        }

        private static bool EnsureDefine(string define)
        {
#if UNITY_6000_0_OR_NEWER
            var t = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
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
    }
}

#endif