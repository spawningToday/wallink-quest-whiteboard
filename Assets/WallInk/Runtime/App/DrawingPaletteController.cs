using System;
using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// World-space tool palette. Works with a controller ray + trigger, with a fingertip poke
    /// from either hand, and with the editor mouse.
    ///
    /// It docks beside the board on the non-writing side so it never covers the writing area;
    /// it can be collapsed to a small "Menu" chip (B / Y button, or its "Hide" button) and
    /// summoned in front of you with the left controller's menu button.
    /// While the pointer is on the palette the app suppresses inking.
    /// </summary>
    public sealed class DrawingPaletteController : MonoBehaviour
    {
        public const int PageDraw = 0, PageBoard = 1, PageInput = 2, PageCalib = 3, PageInfo = 4;
        static readonly string[] PageNames = { "Draw", "Board", "Input", "Calib", "Info" };

        const float W = 0.36f, H = 0.50f;
        const float Left = -0.172f, ColW = 0.054f, Gap = 0.004f, RowH = 0.034f, RowPitch = 0.039f, RowsTop = 0.105f;
        const float PokePress = 0.004f, PokeRelease = 0.015f;

        static readonly Color Accent = new Color(0.20f, 0.52f, 1f, 0.78f);
        static readonly Color Danger = new Color(1f, 0.27f, 0.23f, 0.55f);
        static readonly Color PanelTint = new Color(0.10f, 0.12f, 0.16f, 0.58f);

        AppController _app;
        IXRDevice _device;
        UiPanel _panel;
        UiPanel _mini;
        TextMesh _status, _guide, _info;
        MeshRenderer _cursor;
        int _page = PageDraw;
        bool _collapsed;
        bool _floating;
        bool _placedOnce;
        bool _wasPressed;
        bool _captured;
        UiButton _hover;
        UiButton _holdButton;
        float _holdTime;
        float _refreshTimer;
        bool _prevSecondary, _prevMenu;
        readonly bool[] _pokeDown = new bool[2];

        /// <summary>True while a pointer is on the palette or a press that began on it is still held.</summary>
        public bool PointerOverUi { get; private set; }

        public void ShowPage(int page)
        {
            _page = Mathf.Clamp(page, 0, PageNames.Length - 1);
            _refreshTimer = 0f;
        }

        public void Init(AppController app)
        {
            _app = app;
            _device = app.Device;
            _panel = new UiPanel("Palette", transform, new Vector2(W, H), PanelTint);
            _mini = new UiPanel("Palette Mini", transform, new Vector2(0.09f, 0.05f), PanelTint);
            _mini.AddButton("show", new Rect(-0.037f, -0.017f, 0.074f, 0.034f), "Menu", () => _collapsed = false);
            _mini.Root.gameObject.SetActive(false);

            // Tabs
            float tabW = (0.344f - 5 * Gap) / 6f;
            for (int i = 0; i < PageNames.Length; i++)
            {
                int page = i;
                UiButton tab = _panel.AddButton("tab" + i, new Rect(Left + i * (tabW + Gap), 0.213f, tabW, 0.03f), PageNames[i], () => ShowPage(page));
                tab.toggledFn = () => _page == page;
            }
            _panel.AddButton("hide", new Rect(Left + 5 * (tabW + Gap), 0.213f, tabW, 0.03f), "Hide", () => _collapsed = true);

            _status = _panel.AddLabel(new Vector2(Left, 0.207f), "", 0.0072f, new Color(0.75f, 0.9f, 1f, 1f));
            _guide = _panel.AddLabel(new Vector2(Left, 0.170f), "", 0.0068f, new Color(1f, 0.93f, 0.7f, 1f));
            _info = _panel.AddLabel(new Vector2(Left, RowsTop - RowPitch), "", 0.0066f, Color.white);

            BuildDrawPage();
            BuildBoardPage();
            BuildInputPage();
            BuildCalibPage();
            Add(PageInfo, 0, 0, 3, "resetstats", "Reset counters", () => _app.Drawing.ResetDiagnostics());

            _cursor = InkAssets.NewRenderer("Palette Cursor", transform, InkAssets.Disc, InkAssets.InkMaterial, InkAssets.SortCursor + 2);
            _cursor.transform.localScale = Vector3.one * 0.007f;
            _cursor.enabled = false;
            Refresh();
        }

        // ------------------------------------------------------------------ layout helpers

        static Rect Cell(int row, float col, float span)
        {
            float x = Left + col * (ColW + Gap);
            float w = span * ColW + (span - 1f) * Gap;
            float top = RowsTop - row * RowPitch;
            return new Rect(x, top - RowH, w, RowH);
        }

        UiButton Add(int page, int row, float col, float span, string id, string text, Action onClick)
        {
            UiButton b = _panel.AddButton(id, Cell(row, col, span), text, onClick);
            b.page = page;
            return b;
        }

        void BuildDrawPage()
        {
            DrawingManager d = _app.Drawing;
            // Colour chips: 8 across.
            float chipW = (0.344f - 7 * Gap) / 8f;
            for (int i = 0; i < AppController.Palette.Length; i++)
            {
                Color32 c = AppController.Palette[i];
                var rect = new Rect(Left + i * (chipW + Gap), RowsTop - RowH, chipW, RowH);
                UiButton chip = _panel.AddButton("color" + i, rect, "", () => d.color = c);
                chip.page = PageDraw;
                chip.swatch = c;
                chip.labelFn = () => SameColor(d.color, c) ? "o" : "";
            }
            Add(PageDraw, 1, 0, 2, "pen", "Pen", () => d.tool = DrawTool.Pen).toggledFn = () => d.tool == DrawTool.Pen;
            Add(PageDraw, 1, 2, 2, "line", "Straight line", () => d.tool = DrawTool.Line).toggledFn = () => d.tool == DrawTool.Line;
            Add(PageDraw, 1, 4, 2, "eraser", "Eraser", () => d.tool = DrawTool.Eraser).toggledFn = () => d.tool == DrawTool.Eraser;

            Add(PageDraw, 2, 0, 1, "w-", "-", () => _app.ChangeWidth(-0.5f));
            UiButton wl = Add(PageDraw, 2, 1, 2, "wlabel", "", null);
            wl.labelFn = () => "Width " + d.widthMm.ToString("0.0") + " mm";
            Add(PageDraw, 2, 3, 1, "w+", "+", () => _app.ChangeWidth(0.5f));
            Add(PageDraw, 2, 4, 2, "grid", "", () => _app.CycleGrid()).labelFn = () => "Grid: " + _app.Board.grid;

            Add(PageDraw, 3, 0, 2, "undo", "Undo", () => d.Undo());
            Add(PageDraw, 3, 2, 2, "redo", "Redo", () => d.Redo());
            UiButton clear = Add(PageDraw, 3, 4, 2, "clear", "Clear (hold)", () => d.ClearCurrentSpace());
            clear.holdSeconds = 0.9f;
            clear.baseColor = Danger;

            Add(PageDraw, 4, 0, 3, "space", "", () => _app.ToggleSpace()).labelFn =
                () => d.space == DrawSpace.Surface ? "Mode: Surface" : "Mode: Air (3D)";
            Add(PageDraw, 4, 3, 3, "activation", "", () => _app.ToggleActivation()).labelFn =
                () => d.capture.mode == ActivationMode.ContactLike ? "Ink: Contact-like" : "Ink: Explicit";

            Add(PageDraw, 5, 0, 3, "bring", "Bring board to me", () => _app.BringBoardToMe());
            Add(PageDraw, 5, 3, 3, "move", "Move board", () => _app.MoveBoard());
        }

        void BuildBoardPage()
        {
            Func<bool> choosing = () => _app.ChoosingPlacement;
            Func<bool> placingPoints = () => _app.Placement.Active == PlacementMethod.Points;
            Func<bool> placingOther = () => _app.Placement.Active == PlacementMethod.Detected || _app.Placement.Active == PlacementMethod.Pointer;
            Func<bool> hasBoard = () => _app.Board.Exists && _app.Placement.Active == PlacementMethod.None;

            Add(PageBoard, 0, 0, 3, "create", "Create Whiteboard", () => _app.BeginCreateWhiteboard());
            Add(PageBoard, 0, 3, 3, "realign", "Realign Board", () => _app.Realign()).visibleFn = hasBoard;

            UiButton det = Add(PageBoard, 1, 0, 2, "detected", "Detected", () => _app.StartPlacement(PlacementMethod.Detected));
            det.visibleFn = () => _app.ChoosingPlacement && _app.ScannerAvailable;
            Add(PageBoard, 1, 2, 2, "points", "3+ points", () => _app.StartPlacement(PlacementMethod.Points)).visibleFn = choosing;
            Add(PageBoard, 1, 4, 2, "pointer", "Pointer", () => _app.StartPlacement(PlacementMethod.Pointer)).visibleFn = choosing;

            Add(PageBoard, 1, 0, 2, "finish", "Finish", () => _app.FinishPoints()).visibleFn = placingPoints;
            Add(PageBoard, 1, 2, 2, "undopoint", "Remove last", () => _app.RemoveLastPoint()).visibleFn = placingPoints;
            Add(PageBoard, 1, 4, 2, "cancelpts", "Cancel", () => _app.CancelPlacement()).visibleFn = placingPoints;
            Add(PageBoard, 1, 0, 3, "roomsetup", "Room Setup", () => _app.LaunchRoomSetup()).visibleFn =
                () => _app.Placement.Active == PlacementMethod.Detected && _app.CanLaunchRoomSetup;
            Add(PageBoard, 1, 3, 3, "cancelplace", "Cancel", () => _app.CancelPlacement()).visibleFn = placingOther;

            // Fine adjustment. "Out" moves the board toward you, "In" pushes it into the wall.
            Add(PageBoard, 2, 0, 1, "d+", "Out", () => _app.NudgeBoard(new Vector3(0, 0, 1))).visibleFn = hasBoard;
            Add(PageBoard, 2, 1, 1, "d-", "In", () => _app.NudgeBoard(new Vector3(0, 0, -1))).visibleFn = hasBoard;
            Add(PageBoard, 2, 2, 1, "x-", "Left", () => _app.NudgeBoard(new Vector3(-1, 0, 0))).visibleFn = hasBoard;
            Add(PageBoard, 2, 3, 1, "x+", "Right", () => _app.NudgeBoard(new Vector3(1, 0, 0))).visibleFn = hasBoard;
            Add(PageBoard, 2, 4, 1, "y+", "Up", () => _app.NudgeBoard(new Vector3(0, 1, 0))).visibleFn = hasBoard;
            Add(PageBoard, 2, 5, 1, "y-", "Down", () => _app.NudgeBoard(new Vector3(0, -1, 0))).visibleFn = hasBoard;

            Add(PageBoard, 3, 0, 1, "yaw-", "Yaw -", () => _app.RotateBoard(0, -1, 0)).visibleFn = hasBoard;
            Add(PageBoard, 3, 1, 1, "yaw+", "Yaw +", () => _app.RotateBoard(0, 1, 0)).visibleFn = hasBoard;
            Add(PageBoard, 3, 2, 1, "pitch-", "Tilt -", () => _app.RotateBoard(-1, 0, 0)).visibleFn = hasBoard;
            Add(PageBoard, 3, 3, 1, "pitch+", "Tilt +", () => _app.RotateBoard(1, 0, 0)).visibleFn = hasBoard;
            Add(PageBoard, 3, 4, 1, "roll-", "Roll -", () => _app.RotateBoard(0, 0, -1)).visibleFn = hasBoard;
            Add(PageBoard, 3, 5, 1, "roll+", "Roll +", () => _app.RotateBoard(0, 0, 1)).visibleFn = hasBoard;

            Add(PageBoard, 4, 0, 1, "w-b", "W -", () => _app.ResizeBoard(-1, 0)).visibleFn = hasBoard;
            Add(PageBoard, 4, 1, 1, "w+b", "W +", () => _app.ResizeBoard(1, 0)).visibleFn = hasBoard;
            Add(PageBoard, 4, 2, 1, "h-b", "H -", () => _app.ResizeBoard(0, -1)).visibleFn = hasBoard;
            Add(PageBoard, 4, 3, 1, "h+b", "H +", () => _app.ResizeBoard(0, 1)).visibleFn = hasBoard;
            UiButton step = Add(PageBoard, 4, 4, 2, "step", "", () => _app.CycleStep());
            step.visibleFn = hasBoard;
            step.labelFn = () => "Step: " + _app.StepName;

            UiButton confirm = Add(PageBoard, 5, 0, 3, "confirm", "Confirm Alignment", () => _app.ConfirmBoard());
            confirm.visibleFn = () => hasBoard() && _app.Board.alignment != AlignmentState.Confirmed;
            confirm.baseColor = new Color(0.19f, 0.82f, 0.35f, 0.6f);
            UiButton opacity = Add(PageBoard, 5, 3, 3, "opacity", "", () => _app.CycleOpacity());
            opacity.visibleFn = hasBoard;
            opacity.labelFn = () => "Backing: " + Mathf.RoundToInt(_app.Board.backingOpacity * 100f) + "%";

            Add(PageBoard, 6, 0, 2, "save", "Save Board", () => _app.SaveBoard()).visibleFn = hasBoard;
            Add(PageBoard, 6, 2, 2, "load", "Load Board", () => _app.LoadBoard()).visibleFn = () => BoardPersistence.HasSavedBoard;
            UiButton remove = Add(PageBoard, 6, 4, 2, "remove", "Remove (hold)", () => _app.RemoveBoard());
            remove.visibleFn = hasBoard;
            remove.holdSeconds = 0.9f;
            remove.baseColor = Danger;
        }

        void BuildInputPage()
        {
            Add(PageInput, 0, 0, 2, "in-ctrl", "Controller", () => _app.SetInput(StylusSource.Controller)).toggledFn =
                () => _app.Provider.Source == StylusSource.Controller;
            Add(PageInput, 0, 2, 2, "in-hand", "Fingertip", () => _app.SetInput(StylusSource.Hand)).toggledFn =
                () => _app.Provider.Source == StylusSource.Hand;
            Add(PageInput, 0, 4, 2, "in-pen", "Pen (exp.)", () => _app.SetInput(StylusSource.EstimatedPen)).toggledFn =
                () => _app.Provider.Source == StylusSource.EstimatedPen;

            Add(PageInput, 1, 0, 3, "hand", "", () => _app.ToggleDominantHand()).labelFn = () => "Writing hand: " + _app.DominantHand;
            Add(PageInput, 1, 3, 3, "gesture", "", () => _app.CycleHandGesture()).labelFn = () =>
            {
                switch (_app.HandGesture)
                {
                    case HandDrawGesture.IndexPinch: return "Finger gesture: index pinch";
                    case HandDrawGesture.OtherHandPinch: return "Finger gesture: other hand";
                    default: return "Finger gesture: middle pinch";
                }
            };
            Add(PageInput, 2, 0, 3, "smooth", "", () => _app.CycleSmoothing()).labelFn = () => "Smoothing: " + _app.SmoothingName;
            Add(PageInput, 2, 3, 3, "side", "", () => _app.TogglePaletteSide()).labelFn =
                () => "Palette side: " + (_app.PaletteOnRight ? "right" : "left");
        }

        void BuildCalibPage()
        {
            Add(PageCalib, 0, 0, 3, "pivot", "Tip calibration", () => _app.StartCalibration(CalibrationMode.Pivot));
            Add(PageCalib, 0, 3, 3, "touch", "Touch offset", () => _app.StartCalibration(CalibrationMode.Touch));
            Add(PageCalib, 1, 0, 3, "penquick", "Pen quick calibrate", () => _app.StartCalibration(CalibrationMode.PenQuick)).visibleFn =
                () => _app.Provider.Source == StylusSource.EstimatedPen;
            UiButton reset = Add(PageCalib, 1, 3, 3, "resetcal", "Reset (hold)", () => _app.ResetCalibration());
            reset.holdSeconds = 0.9f;
            reset.baseColor = Danger;

            Add(PageCalib, 2, 0, 1, "so-", "Surf -", () => _app.AdjustSurfaceOffset(-1f));
            Add(PageCalib, 2, 1, 1, "so+", "Surf +", () => _app.AdjustSurfaceOffset(1f));
            Add(PageCalib, 2, 2, 1, "en-", "Enter -", () => _app.AdjustContact(-1f, 0f));
            Add(PageCalib, 2, 3, 1, "en+", "Enter +", () => _app.AdjustContact(1f, 0f));
            Add(PageCalib, 2, 4, 1, "ex-", "Exit -", () => _app.AdjustContact(0f, -1f));
            Add(PageCalib, 2, 5, 1, "ex+", "Exit +", () => _app.AdjustContact(0f, 1f));
            UiButton values = Add(PageCalib, 3, 0, 6, "calvalues", "", null);
            values.labelFn = () => $"Surface offset {_app.Provider.SurfaceOffsetMm:0} mm   contact enter {_app.Drawing.capture.contactEnterMm:0} / exit {_app.Drawing.capture.contactExitMm:0} mm";

            Add(PageCalib, 4, 0, 2, "acc", "Accuracy check", () => _app.StartDiagnostics(DiagnosticMode.Targets));
            Add(PageCalib, 4, 2, 2, "jitter", "Jitter test", () => _app.StartDiagnostics(DiagnosticMode.Jitter));
            Add(PageCalib, 4, 4, 2, "guide", "Writing guide", () => _app.ToggleWritingGuide()).toggledFn = () => _app.WritingGuide;
            Add(PageCalib, 5, 0, 3, "cancelcal", "Cancel procedure", () => _app.CancelProcedures()).visibleFn =
                () => _app.Calibration.Active != CalibrationMode.None || _app.Diagnostics.Active != DiagnosticMode.None;
        }

        static bool SameColor(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        // ------------------------------------------------------------------ per-frame

        public void Tick(float dt)
        {
            HandleButtons();
            UpdatePose();

            UiPanel active = _collapsed ? _mini : _panel;
            _panel.Root.gameObject.SetActive(!_collapsed);
            _mini.Root.gameObject.SetActive(_collapsed);

            bool found = FindPointer(active, out Vector2 local, out bool pressed);
            UiButton over = found ? active.ButtonAt(local) : null;

            if (pressed && !_wasPressed)
            {
                _captured = found;
                if (over != null)
                {
                    if (over.holdSeconds > 0f) { _holdButton = over; _holdTime = 0f; }
                    else over.onClick();
                }
            }
            if (!pressed)
            {
                _captured = false;
                ClearHold();
            }
            else if (_holdButton != null)
            {
                if (over != _holdButton) ClearHold();
                else
                {
                    _holdTime += dt;
                    float p = Mathf.Clamp01(_holdTime / _holdButton.holdSeconds);
                    _holdButton.holdBar.enabled = true;
                    Transform bar = _holdButton.holdBar.transform;
                    bar.localScale = new Vector3(p, 0.14f, 1f);
                    bar.localPosition = new Vector3((p - 1f) * 0.5f, -0.43f, -0.001f);
                    if (p >= 1f)
                    {
                        UiButton b = _holdButton;
                        ClearHold();
                        b.onClick();
                    }
                }
            }
            _wasPressed = pressed;
            PointerOverUi = found || _captured;

            if (over != _hover)
            {
                _hover = over;
                _refreshTimer = 0f;
            }
            _cursor.enabled = found;
            if (found)
            {
                _cursor.transform.SetPositionAndRotation(
                    active.Root.position + active.Root.rotation * new Vector3(local.x, local.y, -0.003f), active.Root.rotation);
            }

            _refreshTimer -= dt;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = 0.2f;
                Refresh();
            }
        }

        void ClearHold()
        {
            if (_holdButton != null) _holdButton.holdBar.enabled = false;
            _holdButton = null;
            _holdTime = 0f;
        }

        void HandleButtons()
        {
            bool secondary = _device.GetButton(Handedness.Left, XRButton.Secondary) || _device.GetButton(Handedness.Right, XRButton.Secondary);
            if (secondary && !_prevSecondary) _collapsed = !_collapsed;
            _prevSecondary = secondary;

            bool menu = _device.GetButton(Handedness.Left, XRButton.Menu);
            if (menu && !_prevMenu)
            {
                _floating = !_floating;
                _collapsed = false;
                if (_floating) PlaceInFrontOfHead();
            }
            _prevMenu = menu;
        }

        void PlaceInFrontOfHead()
        {
            if (!_device.TryGetHeadPose(out Vector3 head, out Quaternion rot)) return;
            Vector3 fwd = rot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 pos = head + fwd * 0.5f + Vector3.down * 0.18f;
            Quaternion r = QuatUtil.LookRotation(fwd, Vector3.up);
            _panel.Root.SetPositionAndRotation(pos, r);
            _mini.Root.SetPositionAndRotation(pos, r);
            _placedOnce = true;
        }

        void UpdatePose()
        {
            WhiteboardSurface board = _app.Board;
            bool dock = !_floating && board.Exists && _app.Placement.Active == PlacementMethod.None;
            if (dock)
            {
                // Beside the board, outside the writing area, on the side opposite the writing
                // hand unless the user chose otherwise; lifted 1.5 cm off the surface.
                float side = _app.PaletteOnRight ? 1f : -1f;
                Quaternion r = board.transform.rotation;
                Vector3 basePos = board.transform.position + r * new Vector3(0f, 0f, -0.015f);
                _panel.Root.SetPositionAndRotation(basePos + r * new Vector3(side * (board.size.x * 0.5f + 0.03f + W * 0.5f), 0f, 0f), r);
                _mini.Root.SetPositionAndRotation(basePos + r * new Vector3(side * (board.size.x * 0.5f + 0.03f + 0.04f), board.size.y * 0.5f - 0.02f, 0f), r);
                _placedOnce = true;
            }
            else if (!_placedOnce)
            {
                PlaceInFrontOfHead();
            }
        }

        /// <summary>
        /// Finds the best pointer on the panel. Priority: a pressing fingertip, then a
        /// controller / mouse ray, then a hovering fingertip.
        /// </summary>
        bool FindPointer(UiPanel panel, out Vector2 local, out bool pressed)
        {
            local = default;
            pressed = false;
            bool found = false;
            int rank = 0;

            for (int h = 0; h < 2; h++)
            {
                var hand = (Handedness)h;
                if (_device.TryGetPointerRay(hand, out Vector3 o, out Vector3 d) &&
                    panel.Raycast(o, d, out Vector2 hit, out float dist) && dist < 4f)
                {
                    bool p = _device.GetTrigger(hand) > 0.5f;
                    int r = p ? 3 : 2;
                    if (r > rank) { rank = r; local = hit; pressed = p; found = true; }
                }

                if (_device.IsHandTracked(hand) && _device.TryGetHandJoint(hand, HandJoint.IndexTip, out Vector3 tip, out _))
                {
                    Vector3 l = panel.ToLocal(tip);
                    bool near = panel.Contains(new Vector2(l.x, l.y), 0.01f) && l.z > -0.03f && l.z < 0.05f;
                    if (!near)
                    {
                        _pokeDown[h] = false;
                        continue;
                    }
                    _pokeDown[h] = _pokeDown[h] ? l.z < PokeRelease : l.z < PokePress;
                    int r = _pokeDown[h] ? 4 : 1;
                    if (r > rank) { rank = r; local = new Vector2(l.x, l.y); pressed = _pokeDown[h]; found = true; }
                }
                else
                {
                    _pokeDown[h] = false;
                }
            }
            return found;
        }

        void Refresh()
        {
            _status.text = UiPanel.Wrap(_app.BuildStatus(), 84);
            string guide = _app.Guide;
            if (_app.Calibration.Active != CalibrationMode.None)
                guide = _app.Calibration.Prompt + (_app.Calibration.Progress > 0f ? $"  [capturing {_app.Calibration.Progress * 100f:0}%]" : "  [get ready]");
            else if (_app.Diagnostics.Active != DiagnosticMode.None)
                guide = _app.Diagnostics.Prompt;
            else if (_app.Placement.Active != PlacementMethod.None)
                guide = _app.Placement.Prompt;
            _guide.text = UiPanel.Wrap(guide, 88);

            bool info = _page == PageInfo;
            _info.gameObject.SetActive(info);
            if (info) _info.text = UiPanel.Wrap(_app.BuildInfo(), 90);

            for (int i = 0; i < _panel.Buttons.Count; i++)
            {
                UiButton b = _panel.Buttons[i];
                bool vis = (b.page < 0 || b.page == _page) && (b.visibleFn == null || b.visibleFn());
                b.SetVisible(vis);
                if (!vis) continue;
                if (b.labelFn != null) b.SetText(b.labelFn());
                Color c = b.swatch ?? (b.toggledFn != null && b.toggledFn() ? Accent : b.baseColor);
                if (b == _hover && b.onClick != null)
                    c = b.swatch.HasValue ? Color.Lerp(c, Color.white, 0.25f) : new Color(c.r, c.g, c.b, Mathf.Min(1f, c.a + 0.22f));
                InkAssets.SetTint(b.background, c);
                if (b.swatch.HasValue) b.label.color = b.swatch.Value.grayscale > 0.6f ? Color.black : Color.white;
            }
        }
    }
}
