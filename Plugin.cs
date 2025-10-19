using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TerminalApi;
using TerminalApi.Classes;
using UnityEngine;

namespace ShipItemsCommand
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("atomic.terminalapi", BepInDependency.DependencyFlags.HardDependency)] // TerminalApi v1.5.x
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "DaanSmoki.LethalCompany.ShipItemsCommand";
        public const string PluginName = "Ship Items Command";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static Harmony Harmony;

        private void Awake()
        {
            Log = Logger;
            Harmony = new Harmony(PluginGuid);

            TerminalApi.TerminalApi.AddCommand("shipitems", new CommandInfo
            {
                Title = "Ship Items",
                Category = "Other",
                Description = "Lists all sellable items currently inside the ship and shows the total value.",
                DisplayTextSupplier = ShipItemScanner.BuildShipItemsText
            });

            Log.LogInfo($"{PluginName} {PluginVersion} loaded and command 'shipitems' registered.");
        }
    }

    // Simple DTO for output
    internal class ItemInfo
    {
        public string Name;
        public int Value;
        public Vector3 Position;
    }

    internal static class ShipItemScanner
    {
        // Types & fields (cached once)
        static readonly Type T_Grabbable = AccessTools.TypeByName("GrabbableObject");
        static readonly FieldInfo F_itemProps = T_Grabbable != null ? T_Grabbable.GetField("itemProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_scrapValue = T_Grabbable != null ? T_Grabbable.GetField("scrapValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_isInShipRoom = T_Grabbable != null ? T_Grabbable.GetField("isInShipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_isInElevator = T_Grabbable != null ? T_Grabbable.GetField("isInElevator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_isHeld = T_Grabbable != null ? T_Grabbable.GetField("isHeld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_isPocketed = T_Grabbable != null ? T_Grabbable.GetField("isPocketed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_playerHeldBy = T_Grabbable != null ? T_Grabbable.GetField("playerHeldBy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;

        static readonly Type T_SOR = AccessTools.TypeByName("StartOfRound");
        static readonly PropertyInfo P_SOR_Instance = T_SOR != null ? T_SOR.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_shipRoom = T_SOR != null ? T_SOR.GetField("shipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_hangarShip = T_SOR != null ? T_SOR.GetField("hangarShip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        static readonly FieldInfo F_shipFloor = T_SOR != null ? T_SOR.GetField("shipFloor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;

        // itemProperties subfields (lazy)
        static FieldInfo F_ip_isScrap, F_ip_scrapValue, F_ip_itemName;

        // small caches to avoid big spikes
        static UnityEngine.Object[] _grabCache = new UnityEngine.Object[0];
        static float _grabCacheAt = -999f;
        const float GrabCacheWindow = 1.5f;

        static Bounds _shipBounds;
        static float _boundsBuiltAt = -999f;
        const float BoundsRefresh = 3f;

        static void EnsureItemPropsCache(object anyIP)
        {
            if (anyIP == null) return;
            var t = anyIP.GetType();
            if (F_ip_isScrap == null) F_ip_isScrap = t.GetField("isScrap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (F_ip_scrapValue == null) F_ip_scrapValue = t.GetField("scrapValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (F_ip_itemName == null) F_ip_itemName = t.GetField("itemName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        static UnityEngine.Object[] GetAllGrabbables()
        {
            if (T_Grabbable == null) return new UnityEngine.Object[0];
            if (Time.time - _grabCacheAt > GrabCacheWindow || _grabCache == null)
            {
                _grabCacheAt = Time.time;
                var arr = UnityEngine.Object.FindObjectsOfType(T_Grabbable) as UnityEngine.Object[];
                _grabCache = arr ?? new UnityEngine.Object[0];
            }
            return _grabCache;
        }

        static Transform GetShipRoot()
        {
            if (T_SOR == null || P_SOR_Instance == null) return null;
            var sor = P_SOR_Instance.GetValue(null);
            if (sor == null) return null;

            GameObject go = F_shipRoom != null ? (F_shipRoom.GetValue(sor) as GameObject) : null;
            if (go != null) return go.transform;
            go = F_hangarShip != null ? (F_hangarShip.GetValue(sor) as GameObject) : null;
            if (go != null) return go.transform;
            go = F_shipFloor != null ? (F_shipFloor.GetValue(sor) as GameObject) : null;
            if (go != null) return go.transform;
            return null;
        }

        static Bounds GetShipBounds()
        {
            if (Time.time - _boundsBuiltAt <= BoundsRefresh && _shipBounds.size != Vector3.zero) return _shipBounds;

            _boundsBuiltAt = Time.time;
            _shipBounds = new Bounds();
            var root = GetShipRoot();
            if (root == null) return _shipBounds;

            var cols = root.GetComponentsInChildren<Collider>(true);
            if (cols != null && cols.Length > 0)
            {
                _shipBounds = cols[0].bounds;
                for (int i = 1; i < cols.Length; i++) _shipBounds.Encapsulate(cols[i].bounds);
                _shipBounds.Expand(0.5f);
            }
            return _shipBounds;
        }

        public static List<ItemInfo> EnumerateShipScrap()
        {
            var all = GetAllGrabbables();
            var results = new List<ItemInfo>();
            if (all == null || all.Length == 0) return results;

            // warm up itemProperties reflection
            if (F_itemProps != null)
            {
                object anyIP = null;
                for (int i = 0; i < all.Length; i++)
                {
                    var o = all[i];
                    if (o != null)
                    {
                        anyIP = F_itemProps.GetValue(o);
                        if (anyIP != null) break;
                    }
                }
                EnsureItemPropsCache(anyIP);
            }

            var shipB = GetShipBounds();
            var shipRoot = GetShipRoot();

            for (int i = 0; i < all.Length; i++)
            {
                var go = all[i];
                if (go == null) continue;

                var comp = go as Component;
                var tr = comp != null ? comp.transform : null;

                // itemProperties
                if (F_itemProps == null) continue;
                var ip = F_itemProps.GetValue(go);
                if (ip == null) continue;

                // must be scrap
                bool isScrap = false;
                if (F_ip_isScrap != null)
                {
                    var v = F_ip_isScrap.GetValue(ip);
                    if (v is bool && (bool)v) isScrap = true;
                }
                if (!isScrap) continue;

                // value
                int value = 0;
                if (F_ip_scrapValue != null)
                {
                    var vv = F_ip_scrapValue.GetValue(ip);
                    if (vv is int) value = Math.Max(value, (int)vv);
                }
                if (F_scrapValue != null)
                {
                    var sv = F_scrapValue.GetValue(go);
                    if (sv is int) value = Math.Max(value, (int)sv);
                }
                if (value <= 0) continue;

                // skip held/pocketed/heldBy
                if (F_isHeld != null)
                {
                    var vh = F_isHeld.GetValue(go);
                    if (vh is bool && (bool)vh) continue;
                }
                if (F_isPocketed != null)
                {
                    var vp = F_isPocketed.GetValue(go);
                    if (vp is bool && (bool)vp) continue;
                }
                if (F_playerHeldBy != null && F_playerHeldBy.GetValue(go) != null) continue;

                // In-ship checks: isInShipRoom || (isInElevator && near ship root) || bounds
                bool inShip = false;
                if (F_isInShipRoom != null)
                {
                    var ir = F_isInShipRoom.GetValue(go);
                    if (ir is bool && (bool)ir) inShip = true;
                }
                if (!inShip && F_isInElevator != null)
                {
                    var ie = F_isInElevator.GetValue(go);
                    if (ie is bool && (bool)ie && shipRoot != null && tr != null)
                    {
                        float dist = Vector3.Distance(tr.position, shipRoot.position);
                        if (dist < 25f) inShip = true;
                    }
                }
                if (!inShip && shipB.size != Vector3.zero && tr != null)
                {
                    inShip = shipB.Contains(tr.position);
                }
                if (!inShip) continue;

                // name
                string name = "Item";
                if (F_ip_itemName != null)
                {
                    var n = F_ip_itemName.GetValue(ip) as string;
                    if (!string.IsNullOrEmpty(n)) name = n;
                }
                else if (comp != null)
                {
                    name = comp.gameObject.name;
                }

                results.Add(new ItemInfo { Name = name, Value = value, Position = tr != null ? tr.position : Vector3.zero });
            }

            return results;
        }

        public static string BuildShipItemsText()
        {
            try
            {
                var items = EnumerateShipScrap();
                if (items == null || items.Count == 0)
                    return "\n— Ship Items —\nNo items detected in the ship.\n\n";

                // Group by item name and count
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (string.IsNullOrEmpty(it.Name)) continue;
                    if (!counts.ContainsKey(it.Name))
                        counts[it.Name] = 1;
                    else
                        counts[it.Name]++;
                }

                // Sort alphabetically by name
                var sorted = new List<string>(counts.Keys);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);

                // Build the output text
                var sb = new StringBuilder();
                sb.AppendLine("\n— Ship Items —");
                sb.AppendLine("Name                Amount");
                sb.AppendLine("----------------------------");

                int total = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    string name = sorted[i];
                    int count = counts[name];
                    total += count;
                    sb.AppendFormat("{0,-20} x {1}\n", name, count);
                }

                sb.AppendLine("----------------------------");
                sb.AppendFormat("TOTAL ITEMS: {0}\n\n", total);
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("BuildShipItemsText error: " + e.Message);
                return "\nShipItems: An error occurred. Check the log.\n\n";
            }
        }
    }
}
