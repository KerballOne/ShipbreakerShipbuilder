using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BBI.Unity.Game
{
    public class AddressableLoader : MonoBehaviour
    {
        [HideInInspector]
        [System.Obsolete("Please use assetGUID instead.")]
        public List<string> refs;

        public string assetGUID = "";
        public string childPath = "";

        [HideInInspector]
        public bool enableChildHardpoints = false;

        public List<string> disabledChildren = new List<string>();

        // Optional: GUID of a RoomType ModulePropertyAsset for DynamicLight.SetSpawnData.
        // Leave empty to use the default (Cockpit).
        public string lightRoomTypeGUID = "";

        // Chance (0–1) that this fixture spawns damaged (flickering). 0 = never.
        public float lightDamagedChance = 0.2f;
        // Chance (0–1) that this fixture spawns broken (off). 0 = never.
        // damagedChance + brokenChance should be <= 1. Normal fills the remainder.
        public float lightBrokenChance = 0.1f;

        // TODO: Not working right now
        [HideInInspector]
        public List<Component> componentsOnChildren = new List<Component>();
        [HideInInspector]
        public List<string> componentsOnChildrenPaths = new List<string>();

        void OnValidate()
        {
            if (refs != null && refs.Count > 0 && assetGUID == "")
            {
                assetGUID = refs[0];
                refs.Clear();
            }
        }
    }
}
