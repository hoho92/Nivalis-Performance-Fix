using System.Linq;
using System.Text;
using Nivalis.UI;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: one-shot list of the active canvases (root or nested, render mode, active graphics below them)
/// and of the canvas each compass (NavigationUI) draws in. A moved or changed UI element makes Unity re-batch its
/// whole canvas (the nearest Canvas above it), so a compass sharing a big canvas makes all of it re-batch at every
/// camera turn. Read-only; run by RouteBench ("probe": true) after the settle.
/// </summary>
internal static class HudProbe
{
    internal static void Dump()
    {
        var sb = new StringBuilder("HUD probe: active canvases (graphics = active Graphic components below, nested canvases included)\n");
        foreach (Canvas c in Object.FindObjectsOfType<Canvas>().Where(c => c.isActiveAndEnabled)
                     .OrderByDescending(c => c.GetComponentsInChildren<Graphic>(false).Length))
        {
            int graphics = c.GetComponentsInChildren<Graphic>(false).Length;
            int nested = c.GetComponentsInChildren<Canvas>(false).Length - 1;
            sb.AppendLine($"  {Path(c.transform)}: {(c.isRootCanvas ? "root" : "nested")} {c.renderMode}, " +
                          $"graphics {graphics}, nested canvases {nested}");
        }
        foreach (NavigationUI nav in Object.FindObjectsOfType<NavigationUI>())
        {
            Canvas own = nav.GetComponentInParent<Canvas>();
            int shared = own != null ? own.GetComponentsInChildren<Graphic>(false).Length : 0;
            int compass = nav.GetComponentsInChildren<Graphic>(false).Length;
            sb.AppendLine($"  compass {Path(nav.transform)} (active {nav.isActiveAndEnabled}): draws in " +
                          $"{(own != null ? Path(own.transform) : "none")}, compass graphics {compass} of {shared} in that canvas");
        }
        int hidden = 0, shown = 0;
        foreach (Animator an in Object.FindObjectsOfType<Animator>())
        {
            if (!an.isActiveAndEnabled || an.GetComponentInParent<Canvas>() == null) continue;
            float alpha = 1;
            for (Transform q = an.transform; q is not null; q = q.parent)
                if (q.GetComponent<CanvasGroup>() is { } g && g != null) alpha *= g.alpha;
            if (alpha > 0) shown++; else hidden++;
            sb.AppendLine($"  UI animator {Path(an.transform)}: alpha {alpha:F2}, update mode {an.updateMode}, culling {an.cullingMode}");
        }
        sb.AppendLine($"  UI animators running: {hidden} invisible (alpha 0), {shown} visible");
        Plugin.Log.LogMessage(sb.ToString());
    }

    /// <summary>Components of the first active objects named as given (with the CanvasGroup alpha above them);
    /// "text:WORDS" = the active texts showing WORDS, with the texts next to them.</summary>
    internal static void Inspect(System.Collections.Generic.IEnumerable<string> names)
    {
        var sb = new StringBuilder("HUD probe: inspect\n");
        var all = Object.FindObjectsOfType<Transform>();
        foreach (string name in names)
        {
            int shown = 0;
            string words = name.StartsWith("text:") ? name.Substring(5) : null;
            foreach (Transform t in all)
            {
                if (words != null)
                {
                    if (!t.gameObject.activeInHierarchy || t.GetComponent<TMPro.TMP_Text>() is not { } tx || tx == null ||
                        tx.text?.Contains(words) != true || ++shown > 4) continue;
                    Transform box = t.parent?.parent ?? t;
                    foreach (TMPro.TMP_Text near in box.GetComponentsInChildren<TMPro.TMP_Text>(true))
                        sb.AppendLine($"  text {Path(near.transform)} (active {near.gameObject.activeInHierarchy}): \"{near.text}\"");
                }
                else if (t.name != name || !t.gameObject.activeInHierarchy || ++shown > 2) continue;
                float alpha = 1;
                for (Transform q = t; q is not null; q = q.parent)
                    if (q.GetComponent<CanvasGroup>() is { } g && g != null) alpha *= g.alpha;
                sb.AppendLine($"  {Path(t)} (alpha {alpha:F2})");
                for (Transform q = t; q is not null && q != t.parent?.parent?.parent; q = q.parent)
                    foreach (Component c in q.GetComponents<Component>())
                    {
                        string state = c.TryCast<Behaviour>() is { } b ? (b.enabled ? "" : " (disabled)") : "";
                        sb.AppendLine($"    {(q == t ? "" : "parent " + q.name + ": ")}{c.GetIl2CppType().FullName}{state}");
                    }
            }
        }
        Plugin.Log.LogMessage(sb.ToString());
    }

    private static string Path(Transform t)
    {
        string p = t.name;
        for (Transform q = t.parent; q is not null; q = q.parent) p = q.name + "/" + p;
        return p;
    }
}
