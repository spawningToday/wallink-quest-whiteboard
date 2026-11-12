using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk.MetaXR
{
    /// <summary>
    /// IBoardAnchor backed by OVRSpatialAnchor.
    ///
    /// Verified against com.meta.xr.sdk.core 209.0.0 source:
    ///  - OVRSpatialAnchor moves its own transform every frame to the located pose, converting
    ///    to world space through Camera.main (the rig's centre eye camera must be tagged MainCamera);
    ///  - it destroys itself if creation fails;
    ///  - there is no "lost / regained" event, so IsTracked is polled.
    ///
    /// Requires Anchor Support to be enabled in the Meta project config (the setup command
    /// does this). An anchor reduces drift by following the headset's map; it does not
    /// guarantee that the board never moves relative to the wall.
    /// </summary>
    public sealed class MetaBoardAnchor : MonoBehaviour, IBoardAnchor
    {
        const float CreateTimeout = 10f;

        GameObject _go;
        OVRSpatialAnchor _anchor;
        float _createTime;
        bool _creating;

        public AnchorState State { get; private set; } = AnchorState.None;
        public string Message { get; private set; } = string.Empty;
        public string PersistentId { get; private set; }

        public void Create(Vector3 position, Quaternion rotation)
        {
            Release();
            _go = new GameObject("WallInk Board Anchor");
            _go.transform.SetPositionAndRotation(position, rotation);
            _anchor = _go.AddComponent<OVRSpatialAnchor>();
            _creating = true;
            _createTime = Time.unscaledTime;
            State = AnchorState.Creating;
            Message = "creating";
        }

        public void Release()
        {
            if (_go != null) Destroy(_go);
            _go = null;
            _anchor = null;
            _creating = false;
            PersistentId = null;
            State = AnchorState.None;
            Message = string.Empty;
        }

        void Update()
        {
            if (State == AnchorState.None || State == AnchorState.Failed || State == AnchorState.Unsupported) return;

            if (_anchor == null) // the component destroyed itself: creation failed
            {
                if (_go != null) Destroy(_go);
                _go = null;
                _creating = false;
                State = AnchorState.Failed;
                Message = "anchor could not be created";
                return;
            }

            if (_creating)
            {
                if (_anchor.Created && _anchor.Localized)
                {
                    _creating = false;
                }
                else if (Time.unscaledTime - _createTime > CreateTimeout)
                {
                    Destroy(_go);
                    _go = null;
                    _anchor = null;
                    _creating = false;
                    State = AnchorState.Failed;
                    Message = "anchor creation timed out";
                    return;
                }
                else
                {
                    return;
                }
            }

            bool tracked = _anchor.IsTracked;
            State = tracked ? AnchorState.Tracked : AnchorState.NotLocalized;
            Message = tracked ? "tracked" : "not located";
        }

        public bool TryGetPose(out Vector3 position, out Quaternion rotation)
        {
            if (_go == null || State != AnchorState.Tracked)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            position = _go.transform.position;
            rotation = _go.transform.rotation;
            return true;
        }

        public void Save(Action<bool> done)
        {
            if (_anchor == null || State != AnchorState.Tracked)
            {
                done?.Invoke(false);
                return;
            }
            OVRSpatialAnchor anchor = _anchor;
            anchor.SaveAnchorAsync().ContinueWith(result =>
            {
                bool ok = result.Success && anchor != null;
                if (ok) PersistentId = anchor.Uuid.ToString();
                else Message = "anchor save failed (" + result.Status + ")";
                done?.Invoke(ok);
            });
        }

        public void Load(string persistentId, Action<bool> done)
        {
            Release();
            if (!Guid.TryParse(persistentId, out Guid uuid))
            {
                State = AnchorState.Failed;
                Message = "saved anchor id is invalid";
                done?.Invoke(false);
                return;
            }

            State = AnchorState.Creating;
            Message = "loading";
            var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
            OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound).ContinueWith(result =>
            {
                if (!result.Success || unbound.Count == 0)
                {
                    State = AnchorState.Failed;
                    Message = result.Success ? "saved anchor not found on this headset" : "anchor load failed (" + result.Status + ")";
                    done?.Invoke(false);
                    return;
                }

                OVRSpatialAnchor.UnboundAnchor found = unbound[0];
                found.LocalizeAsync().ContinueWith(localized =>
                {
                    if (!localized || !found.TryGetPose(out Pose pose))
                    {
                        State = AnchorState.Failed;
                        Message = "saved anchor could not be located in this room";
                        done?.Invoke(false);
                        return;
                    }
                    _go = new GameObject("WallInk Board Anchor");
                    _go.transform.SetPositionAndRotation(pose.position, pose.rotation);
                    _anchor = _go.AddComponent<OVRSpatialAnchor>();
                    found.BindTo(_anchor);
                    _creating = false;
                    PersistentId = persistentId;
                    State = AnchorState.Tracked;
                    Message = "tracked";
                    done?.Invoke(true);
                });
            });
        }
    }
}
