using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    /// <summary>A detected planar region that could host a board.</summary>
    public struct SurfaceCandidate
    {
        public Vector3 center;
        /// <summary>Board convention: local -Z points out of the surface into the room.</summary>
        public Quaternion rotation;
        public Vector2 size;
        public string label;
    }

    public enum SurfaceScanState
    {
        Idle,
        Loading,
        Ready,
        NoRoomData,       // the headset has no room scan for this space
        PermissionDenied, // the user declined spatial-data permission
        Unsupported,      // platform has no scene understanding (e.g. editor without a headset)
        Failed,
    }

    /// <summary>
    /// Automatic surface discovery. Detected planes come from the headset's room scan and are
    /// typically accurate to centimetres, not millimetres, so they are only ever a starting
    /// point that the user confirms and fine-tunes.
    /// </summary>
    public interface ISurfaceScanner
    {
        SurfaceScanState State { get; }
        string StateMessage { get; }
        IReadOnlyList<SurfaceCandidate> Candidates { get; }
        void RequestScan();
        bool CanLaunchRoomSetup { get; }
        void LaunchRoomSetup();
    }

    public enum AnchorState
    {
        None,
        Creating,
        Tracked,
        NotLocalized, // anchor exists but the system cannot currently locate it
        Failed,
        Unsupported,
    }

    /// <summary>
    /// A world-locked reference pose for the board. While tracked, the platform keeps the pose
    /// attached to the physical room as its tracking map is refined. Drift is reduced, not
    /// eliminated.
    /// </summary>
    public interface IBoardAnchor
    {
        AnchorState State { get; }
        string Message { get; }
        /// <summary>Persistent identifier once saved, otherwise null.</summary>
        string PersistentId { get; }
        void Create(Vector3 position, Quaternion rotation);
        void Release();
        bool TryGetPose(out Vector3 position, out Quaternion rotation);
        void Save(Action<bool> done);
        void Load(string persistentId, Action<bool> done);
    }
}
