using System;
using System.Collections.Generic;

namespace WallInk
{
    /// <summary>
    /// Owns every stroke and the undo / redo stacks. Drawing, erasing and clearing are all
    /// recorded as actions that flip stroke visibility, so they undo and redo uniformly.
    /// Performing a new action discards the redo stack.
    /// </summary>
    public sealed class StrokeHistory
    {
        enum Kind { Add, Remove }

        sealed class Step
        {
            public Kind kind;
            public readonly List<StrokeData> strokes = new List<StrokeData>();
            public int groupId; // erase gestures with the same non-zero id merge into one step
        }

        readonly List<StrokeData> _strokes = new List<StrokeData>();
        readonly List<Step> _undo = new List<Step>();
        readonly List<Step> _redo = new List<Step>();
        int _nextId = 1;
        int _nextGroup = 1;

        public int maxUndoSteps = 200;

        /// <summary>Every live stroke (visible or hidden but still reachable by undo/redo), in creation order.</summary>
        public IReadOnlyList<StrokeData> Strokes => _strokes;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>A stroke's visible flag changed; renderers must refresh it.</summary>
        public event Action<StrokeData> VisibilityChanged;
        /// <summary>A stroke can never come back (redo invalidated or undo depth exceeded); renderers may drop it.</summary>
        public event Action<StrokeData> Discarded;
        public event System.Action Changed;

        public int VisibleCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _strokes.Count; i++) if (_strokes[i].visible) n++;
                return n;
            }
        }

        public int NextStrokeId() => _nextId++;
        public int NewEraseGroup() => _nextGroup++;

        /// <summary>Records a finished stroke (already visible).</summary>
        public void AddStroke(StrokeData s)
        {
            InvalidateRedo();
            s.visible = true;
            _strokes.Add(s);
            var a = new Step { kind = Kind.Add };
            a.strokes.Add(s);
            Push(a);
            Changed?.Invoke();
        }

        /// <summary>
        /// Hides the given strokes as one undoable step. Calls sharing the same non-zero
        /// groupId (one continuous eraser drag) merge into a single step.
        /// </summary>
        public void Erase(IList<StrokeData> strokes, int groupId = 0)
        {
            Step a = null;
            if (groupId != 0 && _undo.Count > 0)
            {
                Step top = _undo[_undo.Count - 1];
                if (top.kind == Kind.Remove && top.groupId == groupId) a = top;
            }
            bool any = false;
            for (int i = 0; i < strokes.Count; i++)
            {
                StrokeData s = strokes[i];
                if (!s.visible) continue;
                if (!any)
                {
                    any = true;
                    InvalidateRedo();
                    if (a == null)
                    {
                        a = new Step { kind = Kind.Remove, groupId = groupId };
                        Push(a);
                    }
                }
                s.visible = false;
                a.strokes.Add(s);
                VisibilityChanged?.Invoke(s);
            }
            if (any) Changed?.Invoke();
        }

        /// <summary>Hides every visible stroke of a space (or all when space is null) as one undoable step.</summary>
        public int Clear(StrokeSpace? space = null)
        {
            var list = new List<StrokeData>();
            for (int i = 0; i < _strokes.Count; i++)
            {
                StrokeData s = _strokes[i];
                if (s.visible && (space == null || s.space == space.Value)) list.Add(s);
            }
            if (list.Count > 0) Erase(list);
            return list.Count;
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            Step a = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Apply(a, a.kind != Kind.Add);
            _redo.Add(a);
            Changed?.Invoke();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            Step a = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Apply(a, a.kind == Kind.Add);
            _undo.Add(a);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Forgets everything (used when loading a saved board).</summary>
        public void Reset()
        {
            for (int i = 0; i < _strokes.Count; i++) Discarded?.Invoke(_strokes[i]);
            _strokes.Clear();
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke();
        }

        /// <summary>Adds a stroke restored from storage without creating an undo step.</summary>
        public void AddRestored(StrokeData s)
        {
            s.id = NextStrokeId();
            s.visible = true;
            _strokes.Add(s);
        }

        void Apply(Step a, bool makeVisible)
        {
            for (int i = 0; i < a.strokes.Count; i++)
            {
                StrokeData s = a.strokes[i];
                if (s.visible == makeVisible) continue;
                s.visible = makeVisible;
                VisibilityChanged?.Invoke(s);
            }
        }

        void Push(Step a)
        {
            _undo.Add(a);
            if (_undo.Count > maxUndoSteps)
            {
                // The oldest step becomes permanent. Strokes it erased can never return.
                Step old = _undo[0];
                _undo.RemoveAt(0);
                if (old.kind == Kind.Remove) DiscardAll(old);
            }
        }

        void InvalidateRedo()
        {
            if (_redo.Count == 0) return;
            // An undone "add" leaves its stroke hidden; once redo is gone it is unreachable.
            for (int i = 0; i < _redo.Count; i++)
                if (_redo[i].kind == Kind.Add) DiscardAll(_redo[i]);
            _redo.Clear();
        }

        void DiscardAll(Step a)
        {
            for (int i = 0; i < a.strokes.Count; i++)
            {
                StrokeData s = a.strokes[i];
                if (s.visible) continue; // still shown through a later action; keep it
                if (_strokes.Remove(s)) Discarded?.Invoke(s);
            }
        }
    }
}
