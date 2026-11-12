using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WallInk
{
    [Serializable]
    public class SavedStroke
    {
        public int space;
        public Color32 color;
        public float width;
        public bool line;
        public Vector3 ribbonNormal;
        public Vector3[] points;
    }

    [Serializable]
    public class SavedBoard
    {
        public int version = 1;
        public string savedUtc;
        public Vector2 size;
        public float backingOpacity;
        public int grid;
        /// <summary>Spatial anchor identifier, empty when the board was not anchored.</summary>
        public string anchorId;
        /// <summary>Board pose relative to the anchor.</summary>
        public Vector3 anchorLocalPosition;
        public Quaternion anchorLocalRotation;
        /// <summary>Board pose in the tracking space of the saving session. Only a hint in a later session.</summary>
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public List<SavedStroke> strokes = new List<SavedStroke>();
    }

    [Serializable]
    public class SavedInputCalibration
    {
        public int source;
        public int hand;
        public Vector3 tipOffset;
        public float surfaceOffsetMm;
        public float gripSpreadMm;
        public float gripLengthMm;
    }

    [Serializable]
    public class SavedSettings
    {
        public int version = 1;
        public int dominantHand = 1;
        public int inputSource;
        public int activationMode = (int)ActivationMode.Explicit;
        public int handGesture;
        public float widthMm = 1.5f;
        public float contactEnterMm = 4f;
        public float contactExitMm = 10f;
        public int smoothing = 1;
        public bool paletteOnRight;
        public List<SavedInputCalibration> calibrations = new List<SavedInputCalibration>();
    }

    /// <summary>
    /// Saves and restores one board (strokes in board-local coordinates plus where the board
    /// was) and the app settings, as JSON under Application.persistentDataPath.
    ///
    /// A restored board is never trusted automatically: the app shows it in the
    /// "needs validation" state and writing stays disabled until the user confirms that the
    /// overlay still matches the physical surface, or realigns it.
    /// Raw input samples are not saved; only the rendered points are.
    /// </summary>
    public static class BoardPersistence
    {
        public static string BoardPath => Path.Combine(Application.persistentDataPath, "wallink-board.json");
        public static string SettingsPath => Path.Combine(Application.persistentDataPath, "wallink-settings.json");

        public static bool HasSavedBoard => File.Exists(BoardPath);

        public static SavedBoard Capture(WhiteboardSurface board, StrokeHistory history)
        {
            var sb = new SavedBoard
            {
                savedUtc = DateTime.UtcNow.ToString("o"),
                size = board.size,
                backingOpacity = board.backingOpacity,
                grid = (int)board.grid,
                worldPosition = board.transform.position,
                worldRotation = board.transform.rotation,
                anchorId = string.Empty,
                anchorLocalRotation = new Quaternion(0, 0, 0, 1),
            };
            IReadOnlyList<StrokeData> strokes = history.Strokes;
            for (int i = 0; i < strokes.Count; i++)
            {
                StrokeData s = strokes[i];
                if (!s.visible || s.space != StrokeSpace.Board) continue; // air strokes have no persistent reference
                sb.strokes.Add(new SavedStroke
                {
                    space = (int)s.space,
                    color = s.color,
                    width = s.width,
                    line = s.isStraightLine,
                    ribbonNormal = s.ribbonNormal,
                    points = s.points.ToArray(),
                });
            }
            return sb;
        }

        public static bool Write(SavedBoard board, out string error)
        {
            return WriteJson(BoardPath, JsonUtility.ToJson(board), out error);
        }

        public static SavedBoard Read(out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(BoardPath)) { error = "No saved board found."; return null; }
                var sb = JsonUtility.FromJson<SavedBoard>(File.ReadAllText(BoardPath));
                if (sb == null || sb.size.x <= 0f || sb.size.y <= 0f) { error = "Saved board file is damaged."; return null; }
                return sb;
            }
            catch (Exception e)
            {
                error = "Could not read the saved board: " + e.Message;
                return null;
            }
        }

        public static List<StrokeData> ToStrokes(SavedBoard board)
        {
            var list = new List<StrokeData>(board.strokes.Count);
            for (int i = 0; i < board.strokes.Count; i++)
            {
                SavedStroke ss = board.strokes[i];
                if (ss.points == null || ss.points.Length == 0) continue;
                var s = new StrokeData
                {
                    space = (StrokeSpace)ss.space,
                    color = ss.color,
                    width = ss.width,
                    isStraightLine = ss.line,
                    ribbonNormal = ss.ribbonNormal,
                };
                s.points.AddRange(ss.points);
                s.RecomputeBounds();
                list.Add(s);
            }
            return list;
        }

        public static bool WriteSettings(SavedSettings settings)
        {
            return WriteJson(SettingsPath, JsonUtility.ToJson(settings, true), out _);
        }

        public static SavedSettings ReadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonUtility.FromJson<SavedSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) return s;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("WallInk: settings unreadable, using defaults: " + e.Message);
            }
            return new SavedSettings();
        }

        static bool WriteJson(string path, string json, out string error)
        {
            error = null;
            try
            {
                // Write to a temporary file first so a crash cannot leave a half-written save.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogWarning("WallInk: save failed: " + e.Message);
                return false;
            }
        }
    }
}
