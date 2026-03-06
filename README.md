# VLPipe — Virtual Lab Pipeline
**Unity Editor toolset for building and deploying educational Addressable modules to AWS S3.**

---

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2021.3 LTS or newer |
| com.unity.addressables | 1.19+ |
| AWSSDK.S3 + AWSSDK.Core | Any (via NuGetForUnity) |
| WebGL Build Support | Installed via Unity Hub |

---

## Scripts

| File | Menu Path |
|---|---|
| `ProjectSetupWizard.cs` | `Tools → Virtual Lab → Project Setup` |
| `BuildAndUploadToS3.cs` | `Tools → Virtual Lab → Build And Upload To S3` |

---

## Project Setup Wizard

Run once per module. Complete all 8 steps in order.

| Step | Action |
|---|---|
| 1 | Installs `com.unity.addressables` if missing |
| 2 | Creates the module folder structure under `Assets/Modules/<Board>/<Grade>/<Subject>/<Topic>/` and writes `module_config.json` |
| 3 | Creates Addressables Settings programmatically and opens the Groups window |
| 4 | Sets profile variables — `CustomBaseURL`, `Remote.BuildPath`, `Remote.LoadPath` |
| 5 | Creates **Practice** and **Evaluation** asset groups |
| 6 | Applies LZ4 compression, AppendHash naming, Pack Separately, and Remote paths to both groups |
| 7 | Scans `Assets/Modules` and registers all scenes into the correct group; address is derived from the topic in `module_config.json` |
| 8 | Saves all assets and writes a completion marker |

Closing and reopening the window restores all completed steps automatically.

---

## Build & Upload to S3

`Tools → Virtual Lab → Build And Upload To S3`

1. Reads `module_config.json` to determine the S3 destination path.
2. Confirms and switches the active platform to **WebGL** if needed.
3. Cleans and rebuilds Addressables.
4. Uploads all output files from `ServerData/WebGL/` to S3 with a live progress bar.

**S3 path format:**
```
s3://mhc-embibe-test/Modules/<Board>/<Grade>/<Subject>/<Topic>/WebGL/
```

**Example:**
```
s3://mhc-embibe-test/Modules/CBSE/Grade12/Physics/ComparingEMFOfTwoCells/WebGL/
```

---

## Addressable Key Convention

Scene file names may contain spaces. The Addressable address is always derived from the **topic** field in `module_config.json`, converted to PascalCase.

| Group | Key format | Example |
|---|---|---|
| Practice | `<TopicPascalCase>` | `ComparingEMFOfTwoGivenPrimaryCells` |
| Evaluation | `<TopicPascalCase>_Eval` | `ComparingEMFOfTwoGivenPrimaryCells_Eval` |

---

## S3 Configuration

Credentials are read from environment variables first, with constants as a fallback.

```powershell
# Windows
$env:AWS_ACCESS_KEY_ID     = "YOUR_KEY"
$env:AWS_SECRET_ACCESS_KEY = "YOUR_SECRET"
```

```bash
# macOS / Linux
export AWS_ACCESS_KEY_ID=YOUR_KEY
export AWS_SECRET_ACCESS_KEY=YOUR_SECRET
```

> ⚠ Do not commit credentials to source control.

---

## Folder Structure Created

```
Assets/
└── Modules/
    └── CBSE/
        └── Grade12/
            └── Physics/
                └── <Topic>/
                    ├── module_config.json
                    ├── Scripts/
                    └── Scenes/
                        ├── Practice/
                        └── Evaluation/

ServerData/              ← Addressables build output (git-ignored)
└── WebGL/
    ├── catalog_x.x.bin
    ├── catalog_x.x.hash
    └── *.bundle
```