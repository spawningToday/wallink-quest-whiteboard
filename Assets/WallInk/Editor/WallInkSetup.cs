using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using WallInk.MetaXR;

namespace WallInk.EditorTools
{
    /// <summary>
    /// One-click project configuration and scene generation for Quest 3.
    ///
    /// Menu: WallInk > Setup Project and Scene
    /// CLI:  Unity -batchmode -projectPath . -executeMethod WallInk.EditorTools.WallInkSetup.SetupBatch
    ///
    /// Every API used here was checked against the installed package source:
    /// XR Management 4.5.4, OpenXR 1.17.1, URP 17.5.0, Meta XR Core SDK 209.0.0, MR Utility Kit 209.0.0.
    /// Each step is independent and reports its own success or failure, so one failing step
    /// does not hide the others.
    /// </summary>
    public static class WallInkSetup
    {
        public const string ScenePath = "Assets/WallInk/Scenes/WallInk.unity";
        public const string SimScenePath = "Assets/WallInk/Scenes/WallInkSimulation.unity";

        const string OvrCameraRigGuid = "126d619cf4daa52469682f85c1378b4a";
        const string OvrCameraRigPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        const string MrukPrefabGuid = "c2e00db6d2dfa42ac8d821db8d493a44";
        const string MrukPrefabPath = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";

        const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const string MetaFeatureSetId = "com.meta.openxr.featureset.metaxr";
        const string MetaXRFeatureId = "com.meta.openxr.feature.metaxr";
        const string MetaSubsampledLayoutId = "com.meta.openxr.feature.subsampledLayout";
        const string MetaSpaceWarpId = "com.meta.openxr.feature.spacewarp";
        const string MetaTouchProximityId = "com.meta.openxr.feature.input.oculustouch.proximity";
        const string UnityMetaQuestFeatureId = "com.unity.openxr.feature.metaquest";
        const string OculusTouchProfileId = "com.unity.openxr.feature.input.oculustouch";
        const string TouchPlusProfileId = "com.unity.openxr.feature.input.metaquestplus";

        static readonly List<string> s_log = new List<string>();
        static bool s_restartNeeded;

        [MenuItem("WallInk/Setup Project and Scene", priority = 0)]
        public static void SetupFromMenu()
        {
            bool ok = Run();
            string report = string.Join("\n", s_log);
            EditorUtility.DisplayDialog(ok ? "WallInk setup finished" : "WallInk setup finished with problems",
                report + (s_restartNeeded ? "\n\nRestart the Unity editor once so the input setting takes effect." : "") +
                "\n\nNext: File > Build Profiles > Android > Switch Platform, connect the Quest 3, then Build And Run. See README.md.",
                "OK");
        }

        [MenuItem("WallInk/Create Editor Simulation Scene", priority = 1)]
        public static void CreateSimulationScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Sim Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.transform.position = new Vector3(0f, 1.5f, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.32f, 0.34f, 0.38f, 1f);
            cam.nearClipPlane = 0.05f;
            new GameObject("WallInk").AddComponent<AppController>();
            EnsureFolder(Path.GetDirectoryName(SimScenePath));
            EditorSceneManager.SaveScene(scene, SimScenePath);
            Debug.Log("WallInk: simulation scene saved to " + SimScenePath + ". Press Play; controls are listed in SimulatedXRDevice.cs and README.md.");
        }

        /// <summary>Batch-mode entry point. Waits for the Meta SDK to finish loading, runs setup, then exits.</summary>
        public static void SetupBatch()
        {
            double deadline = EditorApplication.timeSinceStartup + 120.0;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (OVRProjectConfig.CachedProjectConfig == null && EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= tick;
                bool ok = false;
                try { ok = Run(); }
                catch (Exception e) { Debug.LogException(e); }
                Debug.Log("WallInk setup report:\n" + string.Join("\n", s_log));
                EditorApplication.Exit(ok ? 0 : 1);
            };
            EditorApplication.update += tick;
        }

        public static bool Run()
        {
            s_log.Clear();
            s_restartNeeded = false;
            bool ok = true;
            ok &= Step("Player settings (IL2CPP, ARM64, Vulkan, API 32+, linear colour)", ConfigurePlayer);
            ok &= Step("URP render pipeline asset", ConfigureUrp);
            ok &= Step("OpenXR loader and features: Android", () => ConfigureXR(BuildTargetGroup.Android));
            ok &= Step("OpenXR loader and features: Standalone (editor / Link)", () => ConfigureXR(BuildTargetGroup.Standalone));
            ok &= Step("Meta project config (Quest 3, hands, passthrough, scene, anchors)", ConfigureMeta);
            ok &= Step("Scene " + ScenePath, CreateScene);
            AssetDatabase.SaveAssets();
            if (!IsAndroidSupportInstalled())
                s_log.Add("NOTE  Android Build Support is not installed for this editor. Add it in Unity Hub > Installs > (gear) > Add modules > " +
                          "Android Build Support with OpenJDK and Android SDK & NDK Tools before building.");
            return ok;
        }

        /// <summary>
        /// Creates an Assets folder through the AssetDatabase. Creating it with System.IO leaves
        /// it unknown to Unity until the next refresh, and packages that then ask for the same
        /// folder get a duplicate ("XR 1").
        /// </summary>
        static void EnsureFolder(string path)
        {
            path = path.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace(Path.DirectorySeparatorChar, '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static bool Step(string name, Action action)
        {
            try
            {
                action();
                s_log.Add("OK    " + name);
                return true;
            }
            catch (Exception e)
            {
                s_log.Add("FAIL  " + name + ": " + e.Message);
                Debug.LogException(e);
                return false;
            }
        }

        static bool IsAndroidSupportInstalled()
        {
            return BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
        }

        // ------------------------------------------------------------------ player

        static void ConfigurePlayer()
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            if ((int)PlayerSettings.Android.minSdkVersion < 32) PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;
            // No "Made with Unity" splash: go straight into the app.
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;

            if (string.IsNullOrEmpty(PlayerSettings.productName) || PlayerSettings.productName == "Quest Whiteboard App" ||
                PlayerSettings.productName.StartsWith("New Unity Project", StringComparison.Ordinal))
                PlayerSettings.productName = "WallInk";
            string id = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            if (string.IsNullOrEmpty(id) || id.StartsWith("com.DefaultCompany", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("com.UnityTechnologies", StringComparison.OrdinalIgnoreCase))
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.wallink.whiteboard");

            // Active Input Handling has no public setter. OpenXR's interaction profiles need the
            // Input System backend, so switch a legacy-only project to it (0 = old, 1 = new, 2 = both).
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets != null && assets.Length > 0)
            {
                var so = new SerializedObject(assets[0]);
                SerializedProperty handler = so.FindProperty("activeInputHandler");
                if (handler != null && handler.intValue == 0)
                {
                    handler.intValue = 1;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    s_restartNeeded = true;
                }
            }
        }

        // ------------------------------------------------------------------ URP

        static void ConfigureUrp()
        {
            var existing = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (existing == null)
            {
                EnsureFolder("Assets/Settings");
                var data = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(data, "Assets/Settings/WallInk_Renderer.asset");
                ResourceReloader.ReloadAllNullIn(data, UniversalRenderPipelineAsset.packagePath);
                data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                    UniversalRenderPipelineAsset.packagePath + "/Runtime/Data/PostProcessData.asset");
                EditorUtility.SetDirty(data);

                existing = UniversalRenderPipelineAsset.Create(data);
                AssetDatabase.CreateAsset(existing, "Assets/Settings/WallInk_URP.asset");
                GraphicsSettings.defaultRenderPipeline = existing;
            }
            // Settings that matter on Quest and for passthrough compositing: MSAA for thin ink
            // edges, and no HDR / extra full-screen textures (cost, and HDR drops framebuffer alpha).
            existing.msaaSampleCount = 4;
            existing.supportsHDR = false;
            existing.supportsCameraDepthTexture = false;
            existing.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(existing);
            if (QualitySettings.renderPipeline == null) QualitySettings.renderPipeline = existing;
        }

        // ------------------------------------------------------------------ XR

        static void ConfigureXR(BuildTargetGroup group)
        {
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
            if (perTarget == null)
            {
                EnsureFolder("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                AssetDatabase.SaveAssets();
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            XRManagerSettings manager = perTarget.ManagerSettingsForBuildTarget(group);
            if (manager == null) throw new InvalidOperationException("XR manager settings could not be created for " + group);

            bool hasOpenXR = false;
            foreach (XRLoader loader in manager.activeLoaders)
                if (loader is OpenXRLoader) hasOpenXR = true;
            if (!hasOpenXR && !XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderType, group))
                throw new InvalidOperationException("OpenXR loader could not be assigned for " + group + " (is the editor in Play mode?)");
            XRGeneralSettings general = perTarget.SettingsForBuildTarget(group);
            if (general != null) general.InitManagerOnStart = true;

            // Creates the OpenXR settings asset and the feature instances for this target.
            FeatureHelpers.RefreshFeatures(group);

            // The Meta XR feature set carries passthrough, OVRPlugin hand tracking, anchors and scene.
            var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(group, MetaFeatureSetId);
            if (featureSet != null)
            {
                featureSet.isEnabled = true;
                OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(group);
            }

            SetFeature(group, MetaXRFeatureId, true, true);
            SetFeature(group, OculusTouchProfileId, true, true);
            SetFeature(group, TouchPlusProfileId, true, false);
            SetFeature(group, MetaTouchProximityId, true, false);
            // Meta's own setup rules require its space-warp feature to be off on Unity 6.1+.
            SetFeature(group, MetaSpaceWarpId, false, false);
            if (group == BuildTargetGroup.Android)
            {
                SetFeature(group, UnityMetaQuestFeatureId, true, true);
                SetFeature(group, MetaSubsampledLayoutId, true, false);
            }

            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings != null)
            {
                settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                EditorUtility.SetDirty(settings);
            }
        }

        static void SetFeature(BuildTargetGroup group, string featureId, bool enabled, bool required)
        {
            OpenXRFeature feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(group, featureId);
            if (feature == null)
            {
                if (required) throw new InvalidOperationException("OpenXR feature '" + featureId + "' was not found for " + group);
                return;
            }
            if (feature.enabled != enabled) feature.enabled = enabled;
            EditorUtility.SetDirty(feature);
        }

        // ------------------------------------------------------------------ Meta

        static void ConfigureMeta()
        {
            OVRProjectConfig config = OVRProjectConfig.CachedProjectConfig;
            if (config == null)
                throw new InvalidOperationException("The Meta SDK is still initialising. Wait a few seconds and run WallInk > Setup Project and Scene again.");

            config.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>
            {
                OVRProjectConfig.DeviceType.Quest3,
                OVRProjectConfig.DeviceType.Quest3S,
            };
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            config.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            config.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
            OVRProjectConfig.CommitProjectConfig(config);

            // Hand joints are read as OpenXR-skeleton bone positions (see MetaXRDevice).
            OVRRuntimeSettings runtime = OVRRuntimeSettings.GetRuntimeSettings();
            if (runtime != null)
            {
                runtime.HandSkeletonVersion = OVRHandSkeletonVersion.OpenXR;
                OVRRuntimeSettings.CommitRuntimeSettings(runtime);
            }
        }

        // ------------------------------------------------------------------ scene

        static GameObject InstantiatePrefab(string guid, string fallbackPath, string what)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) path = fallbackPath;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new FileNotFoundException(what + " prefab not found at " + path + ". Is the Meta package installed?");
            return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        }

        static void CreateScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode first.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // --- camera rig with passthrough -------------------------------------------------
            GameObject rigGo = InstantiatePrefab(OvrCameraRigGuid, OvrCameraRigPath, "OVRCameraRig");
            rigGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var rig = rigGo.GetComponent<OVRCameraRig>();
            var manager = rigGo.GetComponent<OVRManager>();
            if (rig == null || manager == null) throw new InvalidOperationException("OVRCameraRig prefab is missing OVRCameraRig or OVRManager.");

            manager.isInsightPassthroughEnabled = true;
            var managerSo = new SerializedObject(manager);
            SerializedProperty origin = managerSo.FindProperty("_trackingOriginType");
            if (origin != null) origin.intValue = (int)OVRManager.TrackingOrigin.FloorLevel;
            managerSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);

            foreach (Camera cam in rigGo.GetComponentsInChildren<Camera>(true))
            {
                // Passthrough is composited BEHIND the app (underlay), so the eye buffer must be
                // cleared to transparent black wherever nothing is drawn.
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.05f;
                cam.allowHDR = false;
                UniversalAdditionalCameraData extra = cam.GetUniversalAdditionalCameraData();
                if (extra != null)
                {
                    extra.renderPostProcessing = false;
                    extra.antialiasing = AntialiasingMode.None;
                    EditorUtility.SetDirty(extra);
                }
                // OVRSpatialAnchor resolves world poses through Camera.main.
                if (cam.name == "CenterEyeAnchor") cam.gameObject.tag = "MainCamera";
                else if (cam.gameObject.CompareTag("MainCamera")) cam.gameObject.tag = "Untagged";
                EditorUtility.SetDirty(cam);
            }

            var passthroughGo = new GameObject("Passthrough");
            passthroughGo.transform.SetParent(rigGo.transform, false);
            passthroughGo.AddComponent<OVRPassthroughLayer>(); // always an underlay in SDK 209

            // --- MR Utility Kit --------------------------------------------------------------
            GameObject mrukGo = InstantiatePrefab(MrukPrefabGuid, MrukPrefabPath, "MRUK");
            var mruk = mrukGo.GetComponent<MRUK>();
            if (mruk == null) throw new InvalidOperationException("MRUK prefab has no MRUK component.");
            // Never fall back to a sample room, never load before the user asks, and do not let
            // MRUK move the tracking space (the board has its own spatial anchor).
            if (mruk.SceneSettings == null) mruk.SceneSettings = new MRUK.MRUKSettings();
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            mruk.SceneSettings.LoadSceneOnStartup = false;
            mruk.EnableWorldLock = false;
            EditorUtility.SetDirty(mruk);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mruk);
            PrefabUtility.RecordPrefabInstancePropertyModifications(manager);

            // --- app ---------------------------------------------------------------------------
            var app = new GameObject("WallInk");
            var device = app.AddComponent<MetaXRDevice>();
            device.rig = rig;
            app.AddComponent<MetaSurfaceScanner>();
            app.AddComponent<MetaBoardAnchor>();
            app.AddComponent<AppController>();

            EnsureFolder(Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Scene could not be saved to " + ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        // ------------------------------------------------------------------ build

        /// <summary>CLI: Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod WallInk.EditorTools.WallInkSetup.BuildAndroid</summary>
        [MenuItem("WallInk/Build Android APK", priority = 20)]
        public static void BuildAndroid()
        {
            if (!IsAndroidSupportInstalled())
            {
                Debug.LogError("WallInk: Android Build Support is not installed for this editor (Unity Hub > Installs > Add modules).");
                if (Application.isBatchMode) EditorApplication.Exit(2);
                return;
            }
            if (!File.Exists(ScenePath))
            {
                Debug.LogError("WallInk: scene missing. Run WallInk > Setup Project and Scene first.");
                if (Application.isBatchMode) EditorApplication.Exit(3);
                return;
            }
            Directory.CreateDirectory("Builds");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/WallInk.apk",
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            var sb = new StringBuilder();
            sb.Append("WallInk build: ").Append(report.summary.result).Append(", ")
              .Append(report.summary.totalErrors).Append(" error(s), ")
              .Append(report.summary.totalSize / (1024 * 1024)).Append(" MB -> ").Append(options.locationPathName);
            Debug.Log(sb.ToString());
            if (Application.isBatchMode)
                EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
