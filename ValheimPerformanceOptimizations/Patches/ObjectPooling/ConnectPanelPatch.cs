using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimPerformanceOptimizations.Patches
{
    public static class ConnectPanelPatch
    {
        private static readonly List<string> ObjectsToMove = new List<string>
        {
            "zdossent", "zdosrecv", "clientsendqueue", "datasent", "datarecv",
            "quality", "ping", "fps", "ConnectionListBkg"
        };

        private static Text vpoPoolText;
        
        [HarmonyPatch(typeof(ConnectPanel), "Start"), HarmonyPostfix]
        private static void ConnectPanel_Start_Postfix(ConnectPanel __instance)
        {
            ObjectsToMove.ForEach(objName =>
            {
                var obj = Utils.FindChild(__instance.m_root, objName);
                obj.GetComponent<RectTransform>().anchoredPosition += Vector2.down * 16;
            });

            var zdoPoolTextObject = __instance.m_zdosPool.gameObject;
            var zdoPoolTextPosition = zdoPoolTextObject.GetComponent<RectTransform>().anchoredPosition;
            
            var vpoPoolTextObject = Object.Instantiate(zdoPoolTextObject, __instance.m_root);
            vpoPoolTextObject.GetComponent<RectTransform>().anchoredPosition = zdoPoolTextPosition + Vector2.down * 20;

            vpoPoolTextObject.transform.GetChild(0).GetComponent<Text>().text = "VPO Pool:";

            vpoPoolText = vpoPoolTextObject.GetComponent<Text>();
        }

        [HarmonyPatch(typeof(ConnectPanel), "Update"), HarmonyPostfix]
        private static void ConnectPanel_Update_Postfix(ConnectPanel __instance)
        {
            vpoPoolText.text = $"{ObjectPoolingPatch.UsedPoolObjects} / {ObjectPoolingPatch.FreePoolObjects} / {ObjectPoolingPatch.MaxPooledObjects}";
        }
    }
}