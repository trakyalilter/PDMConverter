using System;
using System.Collections.Generic;
using EdmLib;

namespace PdmApproveConvert
{
    /// <summary>
    /// Thin wrapper over IEdmTaskMgr: task list cached per process, run by name-resolved entry.
    /// </summary>
    internal static class TaskApi
    {
        internal struct TaskEntry
        {
            public string Name;
            public int Id;
            public long Flags;
        }

        private static readonly object Gate = new object();
        private static List<TaskEntry> _cache;

        private static IEdmTaskMgr Mgr(IEdmVault7 vault)
        {
            // Embedded interop turns VARIANT returns into 'dynamic'; take it as object first.
            object util = vault.CreateUtility(EdmUtility.EdmUtil_TaskMgr);
            return (IEdmTaskMgr)util;
        }

        /// <summary>Task list, cached per process. Pass refresh=true to re-read.</summary>
        public static List<TaskEntry> GetTasks(IEdmVault7 vault, bool refresh)
        {
            lock (Gate)
            {
                if (_cache != null && !refresh) return _cache;

                List<TaskEntry> list = new List<TaskEntry>();
                EdmTaskInfo[] tasks = Mgr(vault).GetTasks();
                if (tasks != null)
                {
                    foreach (EdmTaskInfo t in tasks)
                    {
                        TaskEntry e = new TaskEntry();
                        e.Name = t.mbsTaskName;
                        e.Id = t.mlTaskID;
                        e.Flags = t.mllShortTaskInfoFlags;
                        list.Add(e);
                    }
                }
                _cache = list;
                return _cache;
            }
        }

        public static void Run(IEdmVault7 vault, TaskEntry task, EdmSelItem2[] items, int parentWnd)
        {
            EdmTaskInfo info = new EdmTaskInfo();
            info.mbsTaskName = task.Name;
            info.mlTaskID = task.Id;
            info.mllShortTaskInfoFlags = task.Flags;
            Mgr(vault).RunTask(info, items, parentWnd);
        }
    }
}
