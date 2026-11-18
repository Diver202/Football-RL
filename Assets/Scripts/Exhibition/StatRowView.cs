using TMPro;
using UnityEngine;

/// <summary>
/// One row of the full-time breakdown: Red value | label | Blue value, with a split bar underneath
/// that shows each team's share. Lives on the row object in the FinalBuild scene.
/// </summary>
public class StatRowView : MonoBehaviour
{
    public TMP_Text label;
    public TMP_Text redValue;
    public TMP_Text blueValue;
    [Tooltip("Red part of the split bar (anchored from the left).")]
    public RectTransform redBar;
    [Tooltip("Blue part of the split bar (anchored from the right).")]
    public RectTransform blueBar;

    /// <summary>Shows the two values; the bar is split in proportion to redAmount : blueAmount.</summary>
    public void Show(string red, string blue, float redAmount, float blueAmount)
    {
        if (redValue != null) redValue.text = red;
        if (blueValue != null) blueValue.text = blue;

        float total = redAmount + blueAmount;
        float redShare = total > 0f ? Mathf.Clamp01(redAmount / total) : 0.5f;
        if (redBar != null)
        {
            redBar.anchorMin = new Vector2(0f, 0f);
            redBar.anchorMax = new Vector2(redShare, 1f);
            redBar.offsetMin = redBar.offsetMax = Vector2.zero;
        }
        if (blueBar != null)
        {
            blueBar.anchorMin = new Vector2(redShare, 0f);
            blueBar.anchorMax = new Vector2(1f, 1f);
            blueBar.offsetMin = blueBar.offsetMax = Vector2.zero;
        }
    }
}
