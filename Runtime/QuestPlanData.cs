using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestKit
{
    // Stores the choices made in the Quest Planner so a re-run keeps them. Stripped on upload.
    [DisallowMultipleComponent, AddComponentMenu("")]
    public class QuestPlanData : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        public int targetRank = 4;
        public float textureBudgetMB = -1f;
        public string outputName = "";
        public List<Entry> entries = new List<Entry>();

        [Serializable]
        public class Entry
        {
            public string key;
            public string value;
        }

        public bool TryGet(string key, out string value)
        {
            foreach (var e in entries)
                if (e.key == key) { value = e.value; return true; }
            value = null;
            return false;
        }

        public void Set(string key, string value)
        {
            foreach (var e in entries)
                if (e.key == key) { e.value = value; return; }
            entries.Add(new Entry { key = key, value = value });
        }
    }
}
