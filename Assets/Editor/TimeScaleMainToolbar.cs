using UnityEditor.Toolbars;
using UnityEngine;

public class TimeScaleMainToolbar
{
    private const string ToolbarPath = "GPURayTracing/Time Scale";

    [MainToolbarElement(
        ToolbarPath,
        defaultDockPosition = MainToolbarDockPosition.Middle,
        defaultDockIndex = 1)]
    public static MainToolbarElement CreateTimeScaleSlider()
    {
        var content = new MainToolbarContent(
            "Time Scale",
            "Controls Unity simulation speed from paused (0x) to four times speed (4x).");

        return new MainToolbarSlider(
            content,
            Mathf.Clamp(Time.timeScale, 0.0f, 4.0f),
            0.0f,
            4.0f,
            OnTimeScaleChanged);
    }

    private static void OnTimeScaleChanged(float value)
    {
        Time.timeScale = value;
    }
}
