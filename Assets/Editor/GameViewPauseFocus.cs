using System;
using UnityEditor;

[InitializeOnLoad]
public static class GameViewPauseFocus
{
    private const string GameViewTypeName = "UnityEditor.GameView,UnityEditor";

    static GameViewPauseFocus()
    {
        EditorApplication.pauseStateChanged += OnPauseStateChanged;
    }

    private static void OnPauseStateChanged(PauseState state)
    {
        if (state != PauseState.Paused)
        {
            return;
        }

        EditorApplication.delayCall += FocusGameView;
    }

    private static void FocusGameView()
    {
        var gameViewType = Type.GetType(GameViewTypeName);
        if (gameViewType == null)
        {
            return;
        }

        var gameView = EditorWindow.GetWindow(gameViewType);
        gameView.Focus();
        gameView.Repaint();
    }
}
