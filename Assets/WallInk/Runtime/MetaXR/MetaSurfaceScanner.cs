using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace WallInk.MetaXR
{
    /// <summary>
    /// Automatic surface discovery through MR Utility Kit (the headset's Space Setup room scan).
    ///
    /// Verified against com.meta.xr.mrutilitykit 209.0.0 source:
    ///  - plane anchors have their extent in local X/Y (MRUKAnchor.PlaneRect) and their normal
    ///    along local +Z, facing into the room;
    ///  - LoadSceneFromDevice returns NoScenePermission / NoRoomsFound rather than throwing.
    ///
    /// The scene must contain an MRUK object configured with DataSource = Device and
    /// LoadSceneOnStartup = false (the setup command does this); otherwise MRUK would load a
    /// sample room when the headset has none, and boards would be offered on walls that do
    /// not exist.
    /// </summary>
    public sealed class MetaSurfaceScanner : MonoBehaviour, ISurfaceScanner
    {
        readonly List<SurfaceCandidate> _candidates = new List<SurfaceCandidate>();
        bool _loading;

        public SurfaceScanState State { get; private set; } = SurfaceScanState.Idle;
        public string StateMessage { get; private set; } = string.Empty;
        public IReadOnlyList<SurfaceCandidate> Candidates => _candidates;
        public bool CanLaunchRoomSetup => Application.platform == RuntimePlatform.Android;

        public void RequestScan()
        {
            if (_loading) return;
            if (MRUK.Instance == null)
            {
                Fail(SurfaceScanState.Unsupported, "MR Utility Kit is not in the scene.");
                return;
            }
            // The board is held by its own spatial anchor; MRUK's world lock would also move
            // the tracking space each frame, so it is switched off.
            MRUK.Instance.EnableWorldLock = false;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                State = SurfaceScanState.Loading;
                StateMessage = "Waiting for spatial data permission...";
                var callbacks = new UnityEngine.Android.PermissionCallbacks();
                callbacks.PermissionGranted += _ => Load();
                callbacks.PermissionDenied += _ => Fail(SurfaceScanState.PermissionDenied,
                    "Spatial data permission was denied, so detected surfaces are unavailable.");
                UnityEngine.Android.Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission, callbacks);
                return;
            }
#endif
            Load();
        }

        async void Load()
        {
            if (_loading) return;
            _loading = true;
            State = SurfaceScanState.Loading;
            StateMessage = "Loading the room scan...";
            try
            {
                MRUK.LoadDeviceResult result = await MRUK.Instance.LoadSceneFromDevice(false);
                switch (result)
                {
                    case MRUK.LoadDeviceResult.Success:
                        BuildCandidates();
                        State = SurfaceScanState.Ready;
                        StateMessage = _candidates.Count + " surface(s) found.";
                        break;
                    case MRUK.LoadDeviceResult.NoScenePermission:
                        Fail(SurfaceScanState.PermissionDenied, "Spatial data permission is missing, so detected surfaces are unavailable.");
                        break;
                    case MRUK.LoadDeviceResult.NoRoomsFound:
                        Fail(SurfaceScanState.NoRoomData, "This headset has no room scan for this space.");
                        break;
                    default:
                        Fail(SurfaceScanState.Failed, "Room scan could not be loaded (" + result + ").");
                        break;
                }
            }
            catch (Exception e)
            {
                Fail(SurfaceScanState.Failed, "Room scan failed: " + e.Message);
            }
            finally
            {
                _loading = false;
            }
        }

        void Fail(SurfaceScanState state, string message)
        {
            _candidates.Clear();
            State = state;
            StateMessage = message;
        }

        void BuildCandidates()
        {
            _candidates.Clear();
            const MRUKAnchor.SceneLabels excluded =
                MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING |
                MRUKAnchor.SceneLabels.GLOBAL_MESH | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE;

            List<MRUKRoom> rooms = MRUK.Instance.Rooms;
            for (int r = 0; r < rooms.Count; r++)
            {
                List<MRUKAnchor> anchors = rooms[r].Anchors;
                for (int a = 0; a < anchors.Count; a++)
                {
                    MRUKAnchor anchor = anchors[a];
                    if (anchor == null || !anchor.PlaneRect.HasValue || anchor.HasAnyLabel(excluded)) continue;
                    Rect rect = anchor.PlaneRect.Value;
                    if (rect.width < 0.25f || rect.height < 0.2f) continue; // too small to write on

                    Transform t = anchor.transform;
                    _candidates.Add(new SurfaceCandidate
                    {
                        center = t.TransformPoint(new Vector3(rect.center.x, rect.center.y, 0f)),
                        // Board convention: local -Z points out of the surface, so board +Z is the
                        // reverse of the anchor's room-facing +Z.
                        rotation = Quaternion.LookRotation(-t.forward, t.up),
                        size = rect.size,
                        label = anchor.Label.ToString(),
                    });
                }
            }
        }

        public void LaunchRoomSetup()
        {
            if (!CanLaunchRoomSetup) return;
            State = SurfaceScanState.Loading;
            StateMessage = "Room Setup is running...";
            OVRScene.RequestSpaceSetup().ContinueWith(_ => RequestScan());
        }
    }
}
