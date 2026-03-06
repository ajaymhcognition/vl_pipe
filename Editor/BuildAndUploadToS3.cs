// Packages/com.mhcockpit.vlpipe/Editor/BuildAndUploadToS3.cs
// Menu: Tools → Virtual Lab → Build And Upload To S3

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
    /// <summary>
    /// One-click workflow:
    ///   1. Switch active platform to WebGL (if not already, with user confirmation)
    ///   2. Build Addressables
    ///   3. Upload all output files to S3 with a live editor progress bar
    /// </summary>
    public static class BuildAndUploadToS3
    {
        // ═════════════════════════════════════════════════════════════════════
        //  S3 CONFIGURATION
        //  ⚠ SECURITY NOTE: These credentials are sourced from the project
        //  email. In production, prefer environment variables so keys are never
        //  committed to source control:
        //    Windows: $env:AWS_ACCESS_KEY_ID = "..."  /  $env:AWS_SECRET_ACCESS_KEY = "..."
        //    macOS/Linux: export AWS_ACCESS_KEY_ID=...  /  export AWS_SECRET_ACCESS_KEY=...
        //  The constants below are used ONLY when the env vars are absent.
        // ═════════════════════════════════════════════════════════════════════

        private const string S3_BUCKET           = "mhc-embibe-test";
        private const string AWS_REGION          = "ap-south-1";
        private const string FALLBACK_ACCESS_KEY = "AKIA5CBGTAUWLZ2GVA4X";
        private const string FALLBACK_SECRET_KEY = "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";

        private static string AwsAccessKey =>
            Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? FALLBACK_ACCESS_KEY;

        private static string AwsSecretKey =>
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? FALLBACK_SECRET_KEY;

        // ═════════════════════════════════════════════════════════════════════
        //  PATH CONSTANTS — mirrored from ProjectSetupWizard.cs
        // ═════════════════════════════════════════════════════════════════════

        private const string BUILD_ROOT             = "ServerData";
        private const string MODULES_ROOT           = "Assets/Modules";
        private const string MODULE_CONFIG_FILENAME = "module_config.json";
        private const string MONOSCRIPTS_FRAGMENT   = "monoscripts";
        private const string BUILTIN_FRAGMENT       = "unitybuiltinassets";

        private readonly struct UploadFile
        {
            public readonly string LocalPath;
            public readonly string RelativePath;

            public UploadFile(string localPath, string relativePath)
            {
                LocalPath    = localPath;
                RelativePath = relativePath;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  MENU ITEM
        // ═════════════════════════════════════════════════════════════════════

        [MenuItem("Tools/Virtual Lab/Build And Upload", false, 1)]
        public static void TriggerBuildAndUpload() => _ = RunPipelineAsync();

        [MenuItem("Tools/Virtual Lab/Build And Upload", validate = true)]
        [MenuItem("Tools/Virtual Lab/Build And Upload To S3", validate = true)]
        public static bool ValidateTrigger() =>
            !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        // ═════════════════════════════════════════════════════════════════════
        //  PIPELINE ENTRY POINT
        // ═════════════════════════════════════════════════════════════════════

        private static async Task RunPipelineAsync()
        {
            Debug.Log("[VLab S3] ════════════════════════════════════════════");
            Debug.Log("[VLab S3]  Virtual Lab — Build & Upload to S3");
            Debug.Log("[VLab S3] ════════════════════════════════════════════");

            try
            {
                // ── 1. Read module metadata ────────────────────────────────────
                ShowProgress("Reading module config…", 0.02f);
                ModuleConfig config = ReadModuleConfig();
                if (config == null) { ClearProgress(); return; }

                // ── 2. Ensure active platform is WebGL ─────────────────────────
                ShowProgress("Checking build platform…", 0.05f);
                if (!EnsureWebGLPlatform()) { ClearProgress(); return; }

                // ── 3. Build Addressables ──────────────────────────────────────
                ShowProgress("Building Addressables — please wait…", 0.10f);
                string buildOutputFolder = ExecuteAddressablesBuild();
                if (buildOutputFolder == null) { ClearProgress(); return; }

                // ── 4. Upload files to S3 ──────────────────────────────────────
                string buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                string s3Prefix    = BuildS3Prefix(config, buildTarget);
                Debug.Log($"[VLab S3] Destination: s3://{S3_BUCKET}/{s3Prefix}");

                await UploadFolderAsync(buildOutputFolder, s3Prefix);
            }
            catch (AmazonS3Exception s3Ex)
            {
                Debug.LogError($"[VLab S3] S3 error ({s3Ex.ErrorCode}): {s3Ex.Message}");
                EditorUtility.DisplayDialog("S3 Error",
                    $"Error Code : {s3Ex.ErrorCode}\nMessage    : {s3Ex.Message}", "OK");
            }
            catch (AmazonClientException clientEx)
            {
                Debug.LogError($"[VLab S3] AWS client error: {clientEx.Message}");
                EditorUtility.DisplayDialog("AWS Error", clientEx.Message, "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VLab S3] Unexpected error: {ex}");
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
            finally
            {
                // Always clear the progress bar — even on exceptions.
                ClearProgress();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  STEP 1 — READ MODULE CONFIG
        // ═════════════════════════════════════════════════════════════════════

        [Serializable]
        private class ModuleConfig
        {
            public string board, grade, subject, topic, createdDate;
        }

        private static ModuleConfig ReadModuleConfig()
        {
            string modulesAbsPath = Path.Combine(Application.dataPath, "Modules");

            if (!Directory.Exists(modulesAbsPath))
            {
                Debug.LogError($"[VLab S3] '{MODULES_ROOT}' not found. " +
                               "Run Project Setup Wizard Step 2 first.");
                return null;
            }

            string[] configFiles = Directory.GetFiles(
                modulesAbsPath, MODULE_CONFIG_FILENAME, SearchOption.AllDirectories);

            if (configFiles.Length == 0)
            {
                Debug.LogError($"[VLab S3] No '{MODULE_CONFIG_FILENAME}' found. " +
                               "Run Project Setup Wizard Step 2 first.");
                return null;
            }

            if (configFiles.Length > 1)
                Debug.LogWarning($"[VLab S3] {configFiles.Length} module configs found — " +
                                 $"using: {configFiles[0]}");

            var config = JsonUtility.FromJson<ModuleConfig>(File.ReadAllText(configFiles[0]));

            if (config == null
                || string.IsNullOrEmpty(config.board)
                || string.IsNullOrEmpty(config.grade)
                || string.IsNullOrEmpty(config.subject)
                || string.IsNullOrEmpty(config.topic))
            {
                Debug.LogError("[VLab S3] module_config.json is corrupt. " +
                               "Re-run Setup Wizard Step 2.");
                return null;
            }

            Debug.Log($"[VLab S3] Module: {config.board} / {config.grade} / " +
                      $"{config.subject} / {config.topic}");
            return config;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  STEP 2 — ENSURE WEBGL PLATFORM
        //
        //  Addressable bundles are platform-specific. If the active target is not
        //  WebGL the user is shown a confirmation dialog before Unity switches.
        //  SwitchActiveBuildTarget() is synchronous — it blocks until reimport
        //  is complete, so the build that follows targets the correct platform.
        // ═════════════════════════════════════════════════════════════════════

        private static bool EnsureWebGLPlatform()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
            {
                Debug.Log("[VLab S3] Platform: WebGL ✓");
                return true;
            }

            string current = EditorUserBuildSettings.activeBuildTarget.ToString();
            Debug.LogWarning($"[VLab S3] Active platform is '{current}', not WebGL.");

            bool confirm = EditorUtility.DisplayDialog(
                "Switch Platform to WebGL?",
                $"Current platform: {current}\n\n" +
                "Addressables must be built for WebGL.\n" +
                "Switch now? (This may take a minute while Unity reimports assets.)",
                "Switch to WebGL",
                "Cancel");

            if (!confirm)
            {
                Debug.LogWarning("[VLab S3] Platform switch cancelled — pipeline aborted.");
                return false;
            }

            ShowProgress("Switching platform to WebGL — reimporting assets…", 0.07f);
            Debug.Log("[VLab S3] Switching to WebGL…");

            bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.WebGL, BuildTarget.WebGL);

            if (!ok)
            {
                Debug.LogError("[VLab S3] Platform switch to WebGL failed.");
                EditorUtility.DisplayDialog("Platform Switch Failed",
                    "Unity could not switch to WebGL. " +
                    "Check that the WebGL Build Support module is installed " +
                    "via Unity Hub → Installs → Add Modules.", "OK");
                return false;
            }

            Debug.Log("[VLab S3] Platform switched to WebGL. ✓");
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  STEP 3 — ADDRESSABLES BUILD
        // ═════════════════════════════════════════════════════════════════════

        private static string ExecuteAddressablesBuild()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[VLab S3] Addressables Settings not found. " +
                               "Run the Project Setup Wizard first.");
                return null;
            }

            // Clean first so stale bundles from a previous platform are removed.
            ShowProgress("Cleaning previous Addressables output…", 0.12f);
            Debug.Log("[VLab S3] Cleaning previous build…");
            AddressableAssetSettings.CleanPlayerContent(settings.ActivePlayerDataBuilder);

            ShowProgress("Building Addressables — this may take several minutes…", 0.18f);
            Debug.Log("[VLab S3] Building Addressables…");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (!string.IsNullOrEmpty(result.Error))
            {
                Debug.LogError($"[VLab S3] Addressables build failed:\n{result.Error}");
                EditorUtility.DisplayDialog("Build Failed",
                    $"Addressables build failed:\n\n{result.Error}", "OK");
                return null;
            }

            Debug.Log($"[VLab S3] Build completed in {result.Duration:F2}s. ✓");

            string projectRoot  = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string buildTarget  = EditorUserBuildSettings.activeBuildTarget.ToString();
            string outputFolder = Path.Combine(projectRoot, BUILD_ROOT, buildTarget);

            if (!Directory.Exists(outputFolder))
            {
                Debug.LogError($"[VLab S3] Output folder not found: {outputFolder}\n" +
                               "Verify Remote.BuildPath = 'ServerData/[BuildTarget]' " +
                               "in your Addressables profile (Setup Wizard Step 4).");
                return null;
            }

            string[] catalogFiles = Directory.GetFiles(
                outputFolder,
                "catalog*.json",
                SearchOption.TopDirectoryOnly);

            if (catalogFiles.Length == 0)
            {
                Debug.LogError(
                    $"[VLab S3] No catalog JSON found in build output: {outputFolder}\n" +
                    "Expected files like catalog.json or catalog_mhcockpit.json.\n" +
                    "Check Addressables global catalog settings in Setup Wizard Step 6.");
                return null;
            }

            int count = Directory.GetFiles(outputFolder, "*", SearchOption.AllDirectories).Length;
            Debug.Log($"[VLab S3] Output folder: {outputFolder}  ({count} file(s))");
            ShowProgress($"Build complete — {count} file(s) ready to upload.", 0.28f);
            return outputFolder;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  STEP 4 — S3 UPLOAD
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds the S3 key prefix for this module's upload folder.
        ///
        /// The topic is converted to PascalCase using the same ToAddressableKey() logic
        /// that ProjectSetupWizard uses when registering scenes in the Addressables Groups window.
        /// This ensures the S3 folder name matches the Addressable key exactly, so the
        /// Dashboard can derive the correct catalog URL from the key at runtime.
        ///
        /// Example:
        ///   topic (raw)    : "Comparing EMF of two given primary cells"
        ///   topic (S3 path): "ComparingEMFOfTwoGivenPrimaryCells"
        ///   S3 prefix      : Modules/CBSE/Grade12/Physics/ComparingEMFOfTwoGivenPrimaryCells/WebGL/
        /// </summary>
        private static string BuildS3Prefix(ModuleConfig cfg, string buildTarget) =>
            $"Modules/{cfg.board}/{cfg.grade}/{cfg.subject}/{ToAddressableKey(cfg.topic)}/{buildTarget}/";

        /// <summary>
        /// Converts a raw topic string to a PascalCase Addressable key segment.
        /// Identical to the same method in ProjectSetupWizard.cs — kept in sync manually.
        ///
        /// Rules:
        ///   • Splits on spaces, hyphens, and underscores.
        ///   • Capitalises the first character of each word segment.
        ///   • Preserves existing upper-case runs (e.g. "EMF" stays "EMF").
        ///   • Strips all non-alphanumeric characters from each segment.
        ///
        /// Examples:
        ///   "Comparing EMF of two given primary cells" → "ComparingEMFOfTwoGivenPrimaryCells"
        ///   "simple pendulum"                         → "SimplePendulum"
        ///   "Ohm's Law - Experiment 1"                → "OhmsLawExperiment1"
        /// </summary>
        private static string ToAddressableKey(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic)) return topic;

            string[] words = topic.Split(
                new[] { ' ', '-', '_', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            var sb = new System.Text.StringBuilder(topic.Length);
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

        private static async Task UploadFolderAsync(string localFolder, string s3Prefix)
        {
            List<UploadFile> uploadFiles = BuildUploadManifest(localFolder);

            if (uploadFiles.Count == 0)
            {
                Debug.LogWarning("[VLab S3] Build output folder is empty — nothing to upload.");
                return;
            }

            Debug.Log($"[VLab S3] Uploading {uploadFiles.Count} file(s)…");
            Debug.Log($"[VLab S3] Bucket : {S3_BUCKET}");
            Debug.Log($"[VLab S3] Prefix : {s3Prefix}");
            Debug.Log("[VLab S3] ────────────────────────────────────────────");

            var credentials = new BasicAWSCredentials(AwsAccessKey, AwsSecretKey);
            using var s3Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(AWS_REGION));
            using var transfer = new TransferUtility(s3Client);

            var succeeded  = new List<string>();
            var failedList = new List<string>();

            // Upload phase occupies progress 0.30 → 1.00
            const float UPLOAD_START = 0.30f;
            const float UPLOAD_RANGE = 0.70f;
            float       slicePerFile = UPLOAD_RANGE / Math.Max(uploadFiles.Count, 1);

            for (int i = 0; i < uploadFiles.Count; i++)
            {
                UploadFile item     = uploadFiles[i];
                string filePath     = item.LocalPath;
                string fileName     = Path.GetFileName(filePath);
                string relativePath = item.RelativePath;
                string s3Key    = s3Prefix + relativePath;
                long   fileSize = new FileInfo(filePath).Length;

                float fileBaseProgress = UPLOAD_START + i * slicePerFile;

                // ── Progress bar: start of this file ──────────────────────────
                ShowProgress(
                    $"Uploading  [{i + 1} / {uploadFiles.Count}]  {fileName}",
                    fileBaseProgress,
                    $"s3://{S3_BUCKET}/{s3Key}  ({FormatBytes(fileSize)})");

                Debug.Log($"[VLab S3] [{i + 1}/{uploadFiles.Count}] {fileName}" +
                          $"  ({FormatBytes(fileSize)})  →  {s3Key}");

                try
                {
                    long lastReported = 0;
                    long reportEvery  = Math.Max(1, fileSize / 20); // ~5 % steps

                    var request = new TransferUtilityUploadRequest
                    {
                        BucketName  = S3_BUCKET,
                        FilePath    = filePath,
                        Key         = s3Key,
                        ContentType = ResolveContentType(filePath)
                        // CannedACL is intentionally omitted.
                        // The bucket uses Object Ownership = BucketOwnerEnforced,
                        // which disables all ACLs. Setting any CannedACL value causes
                        // s3:PutObjectAcl to be called, which the IAM policy denies.
                    };

                    request.UploadProgressEvent += (_, args) =>
                    {
                        if (args.TransferredBytes - lastReported < reportEvery) return;
                        lastReported = args.TransferredBytes;

                        float withinFile = fileSize > 0
                            ? (float)args.TransferredBytes / fileSize : 1f;
                        float overall = fileBaseProgress + withinFile * slicePerFile;

                        ShowProgress(
                            $"Uploading  [{i + 1} / {uploadFiles.Count}]  " +
                            $"{fileName}  {args.PercentDone}%",
                            overall,
                            $"{FormatBytes(args.TransferredBytes)} / {FormatBytes(fileSize)}" +
                            $"   →   s3://{S3_BUCKET}/{s3Key}");
                    };

                    await transfer.UploadAsync(request);

                    succeeded.Add(fileName);
                    Debug.Log($"[VLab S3]   ✓  {fileName}  [{FormatBytes(fileSize)}]");
                }
                catch (AmazonS3Exception s3Ex)
                {
                    failedList.Add(fileName);
                    Debug.LogError($"[VLab S3]   ✗  {fileName} — " +
                                   $"S3 {s3Ex.ErrorCode}: {s3Ex.Message}");
                }
                catch (Exception ex)
                {
                    failedList.Add(fileName);
                    Debug.LogError($"[VLab S3]   ✗  {fileName} — {ex.Message}");
                }
            }

            // ── Summary ────────────────────────────────────────────────────────
            Debug.Log("[VLab S3] ────────────────────────────────────────────");

            if (failedList.Count == 0)
            {
                ShowProgress("Upload complete! ✓", 1f,
                    $"{succeeded.Count} file(s) → s3://{S3_BUCKET}/{s3Prefix}");

                Debug.Log($"[VLab S3] ✓ Upload completed successfully.\n" +
                          $"  {succeeded.Count} file(s) → s3://{S3_BUCKET}/{s3Prefix}");

                await Task.Delay(1200); // let the user read the 100 % bar
                EditorUtility.DisplayDialog(
                    "Upload Complete ✓",
                    $"All {succeeded.Count} file(s) uploaded successfully.\n\n" +
                    $"Bucket : {S3_BUCKET}\n" +
                    $"Prefix : {s3Prefix}",
                    "OK");
            }
            else
            {
                ShowProgress($"Finished with {failedList.Count} error(s).", 1f,
                    $"Succeeded: {succeeded.Count}   Failed: {failedList.Count}");

                Debug.LogWarning($"[VLab S3] Upload finished with errors.\n" +
                                 $"  Succeeded : {succeeded.Count}\n" +
                                 $"  Failed    : {failedList.Count}\n" +
                                 $"  Files     : {string.Join(", ", failedList)}");

                await Task.Delay(1200);
                EditorUtility.DisplayDialog(
                    "Upload Finished with Errors",
                    $"Succeeded : {succeeded.Count}\n" +
                    $"Failed    : {failedList.Count}\n\n" +
                    $"Failed files:\n  {string.Join("\n  ", failedList)}\n\n" +
                    "See the Console window for full error details.",
                    "OK");
            }
        }

        private static List<UploadFile> BuildUploadManifest(string localFolder)
        {
            var manifest = new List<UploadFile>();

            if (!Directory.Exists(localFolder))
                return manifest;

            string normalisedRoot = localFolder.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (string filePath in Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath
                    .Substring(normalisedRoot.Length + 1)
                    .Replace('\\', '/');

                manifest.Add(new UploadFile(filePath, relativePath));
            }

            AppendFallbackBuiltInBundles(manifest);
            return manifest;
        }

        private static void AppendFallbackBuiltInBundles(List<UploadFile> manifest)
        {
            bool hasMonoscripts = manifest.Any(f =>
                Path.GetFileName(f.LocalPath)
                    .IndexOf(MONOSCRIPTS_FRAGMENT, StringComparison.OrdinalIgnoreCase) >= 0);

            bool hasBuiltinAssets = manifest.Any(f =>
                Path.GetFileName(f.LocalPath)
                    .IndexOf(BUILTIN_FRAGMENT, StringComparison.OrdinalIgnoreCase) >= 0);

            if (hasMonoscripts && hasBuiltinAssets)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            string fallbackDir = Path.Combine(
                projectRoot,
                "Library",
                "com.unity.addressables",
                "aa",
                buildTarget);

            if (!Directory.Exists(fallbackDir))
            {
                Debug.LogWarning(
                    $"[VLab S3] Fallback bundle folder not found: {fallbackDir}\n" +
                    "If child bundles fail to load at runtime, run Setup Wizard Step 6 " +
                    "to force built-in groups to Remote paths, then rebuild.");
                return;
            }

            string[] candidates = Directory.GetFiles(
                fallbackDir,
                "*.bundle",
                SearchOption.AllDirectories);

            foreach (string bundlePath in candidates)
            {
                string fileName = Path.GetFileName(bundlePath);

                bool isMonoscripts = fileName.IndexOf(
                    MONOSCRIPTS_FRAGMENT,
                    StringComparison.OrdinalIgnoreCase) >= 0;

                bool isBuiltin = fileName.IndexOf(
                    BUILTIN_FRAGMENT,
                    StringComparison.OrdinalIgnoreCase) >= 0;

                bool needed = (!hasMonoscripts && isMonoscripts)
                              || (!hasBuiltinAssets && isBuiltin);

                if (!needed)
                    continue;

                bool alreadyPresent = manifest.Any(f =>
                    Path.GetFileName(f.LocalPath).Equals(fileName, StringComparison.OrdinalIgnoreCase));

                if (alreadyPresent)
                    continue;

                manifest.Add(new UploadFile(bundlePath, fileName));
                Debug.Log(
                    $"[VLab S3] Added fallback built-in bundle to upload manifest: {fileName}");
            }

            bool stillMissingMonoscripts = !manifest.Any(f =>
                Path.GetFileName(f.LocalPath)
                    .IndexOf(MONOSCRIPTS_FRAGMENT, StringComparison.OrdinalIgnoreCase) >= 0);

            bool stillMissingBuiltin = !manifest.Any(f =>
                Path.GetFileName(f.LocalPath)
                    .IndexOf(BUILTIN_FRAGMENT, StringComparison.OrdinalIgnoreCase) >= 0);

            if (stillMissingMonoscripts || stillMissingBuiltin)
            {
                Debug.LogWarning(
                    "[VLab S3] Built-in dependency bundles are still missing from upload manifest.\n" +
                    "This can cause remote scene load failures. Re-run Project Setup Step 6 and rebuild.");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  EDITOR PROGRESS BAR
        //  DisplayProgressBar is thread-safe from Unity 2021+ but must be called
        //  on the main thread. In this script every call originates from the
        //  async pipeline which is driven by the editor's synchronisation context,
        //  so all progress calls land on the main thread automatically.
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates the editor-wide progress bar.
        /// <paramref name="progress"/> must be in [0, 1].
        /// </summary>
        private static void ShowProgress(string title, float progress, string info = "")
        {
            string body = string.IsNullOrEmpty(info) ? title : $"{title}\n{info}";
            EditorUtility.DisplayProgressBar(
                "Virtual Lab — Build & Upload to S3",
                body,
                Mathf.Clamp01(progress));
        }

        /// <summary>Removes the editor progress bar.</summary>
        private static void ClearProgress() =>
            EditorUtility.ClearProgressBar();

        // ═════════════════════════════════════════════════════════════════════
        //  UTILITIES
        // ═════════════════════════════════════════════════════════════════════

        private static string ResolveContentType(string filePath) =>
            Path.GetExtension(filePath).ToLowerInvariant() switch
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

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024)     return $"{bytes / 1_024.0:F1} KB";
            return $"{bytes} B";
        }
    }
}

#endif // UNITY_EDITOR && ADDRESSABLES_INSTALLED
