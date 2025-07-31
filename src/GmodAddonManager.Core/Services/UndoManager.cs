using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public class UndoManager
    {
        private readonly Stack<UndoAction> undoStack;
        private const int MaxUndoStackSize = 50;
        
        public UndoManager()
        {
            undoStack = new Stack<UndoAction>();
        }
        
        /// <summary>
        /// Undo操作を記録
        /// </summary>
        public void RecordAction(UndoAction action)
        {
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