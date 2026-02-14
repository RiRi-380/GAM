using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public class UndoManager
    {
        private readonly Stack<UndoAction> undoStack;
        private const int MaxUndoStackSize = 50;
        private int suppressRecordingCount;
        
        public UndoManager()
        {
            undoStack = new Stack<UndoAction>();
        }
        
        /// <summary>
        /// Undo操作を記録
        /// </summary>
        public void RecordAction(UndoAction action)
        {
            if (IsRecordingSuppressed)
            {
                return;
            }

            undoStack.Push(action);
            
            // スタックサイズ制限
            while (undoStack.Count > MaxUndoStackSize)
            {
                var removed = undoStack.ToArray()[undoStack.Count - 1];
                var newStack = new Stack<UndoAction>(undoStack.Take(undoStack.Count - 1).Reverse());
                undoStack.Clear();
                foreach (var item in newStack)
                {
                    undoStack.Push(item);
                }
            }
        }

        /// <summary>
        /// 指定した操作をUndoスタックの先頭に移動
        /// </summary>
        public IDisposable SuppressRecording()
        {
            Interlocked.Increment(ref suppressRecordingCount);
            return new RecordingSuppression(this);
        }

        private void EndSuppressRecording()
        {
            Interlocked.Decrement(ref suppressRecordingCount);
        }

        private bool IsRecordingSuppressed => Volatile.Read(ref suppressRecordingCount) > 0;

        private sealed class RecordingSuppression : IDisposable
        {
            private UndoManager? owner;

            public RecordingSuppression(UndoManager owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                owner?.EndSuppressRecording();
                owner = null;
            }
        }

        /// <summary>
        /// Move the specified action to the top of the undo stack.
        /// </summary>
        public void MoveToTop(UndoAction? action)
        {
            if (action == null || undoStack.Count == 0)
            {
                return;
            }

            if (ReferenceEquals(undoStack.Peek(), action))
            {
                return;
            }

            var list = undoStack.ToList(); // 先頭が最新
            var removed = list.Remove(action);
            if (!removed)
            {
                var index = list.FindIndex(item => item.Id == action.Id);
                if (index < 0)
                {
                    return;
                }
                list.RemoveAt(index);
            }

            undoStack.Clear();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                undoStack.Push(list[i]);
            }
            undoStack.Push(action);
        }
        
        /// <summary>
        /// 最後の操作を取得（削除はしない）
        /// </summary>
        public UndoAction? PeekLastAction()
        {
            return undoStack.Count > 0 ? undoStack.Peek() : null;
        }
        
        /// <summary>
        /// 最後の操作を取得して削除
        /// </summary>
        public UndoAction? PopLastAction()
        {
            if (undoStack.Count > 0)
            {
                var action = undoStack.Pop();
                return action;
            }
            return null;
        }
        
        /// <summary>
        /// Undo可能な操作があるか
        /// </summary>
        public bool CanUndo => undoStack.Count > 0;
        
        /// <summary>
        /// Undo履歴を取得
        /// </summary>
        public List<UndoAction> GetHistory(int count = 10)
        {
            return undoStack.Take(count).ToList();
        }
        
        /// <summary>
        /// 履歴をクリア
        /// </summary>
        public void Clear()
        {
            undoStack.Clear();
        }
    }
}
