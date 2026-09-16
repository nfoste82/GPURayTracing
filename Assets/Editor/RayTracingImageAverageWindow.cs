using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public sealed class RayTracingImageAverageWindow : EditorWindow
{
    private readonly List<string> _imagePaths = new List<string>();
    private readonly List<string> _sampleCounts = new List<string>();
    private Vector2 _scrollPosition;
    private int _width;
    private int _height;

    [MenuItem("Tools/Ray Tracing/Windows/Combine PNG Images")]
    public static void Open()
    {
        GetWindow<RayTracingImageAverageWindow>("Combine PNG Images");
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Adds matching PNG images and writes their per-channel arithmetic average. Enter an optional sample count for each image to apply weighted averaging; blank fields use weight 1. " +
            "Note that this tool does not guarantee that images used as inputs are unbiased.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add PNG", GUILayout.Height(24)))
            {
                AddImage();
            }

            using (new EditorGUI.DisabledScope(_imagePaths.Count == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Height(24)))
                {
                    _imagePaths.Clear();
                    _sampleCounts.Clear();
                    _width = 0;
                    _height = 0;
                }
            }
        }

        EditorGUILayout.LabelField(
            _imagePaths.Count == 0
                ? "No images selected."
                : $"{_imagePaths.Count} image(s), {_width}x{_height}",
            EditorStyles.boldLabel);

        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.MinHeight(100));
        for (int index = 0; index < _imagePaths.Count; index++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(Path.GetFileName(_imagePaths[index]), GUILayout.MinWidth(120));
                _sampleCounts[index] = EditorGUILayout.TextField(
                    _sampleCounts[index],
                    GUILayout.Width(90));
                GUILayout.Label("samples", GUILayout.Width(52));
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    _imagePaths.RemoveAt(index);
                    _sampleCounts.RemoveAt(index);
                    if (_imagePaths.Count == 0)
                    {
                        _width = 0;
                        _height = 0;
                    }
                    index--;
                }
            }
        }
        EditorGUILayout.EndScrollView();

        using (new EditorGUI.DisabledScope(_imagePaths.Count < 2))
        {
            if (GUILayout.Button("Average Images", GUILayout.Height(28)))
            {
                AverageImages();
            }
        }
    }

    private void AddImage()
    {
        string path = EditorUtility.OpenFilePanel("Add PNG Image", string.Empty, "png");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog("Invalid Image", "Only .png files can be added.", "OK");
            return;
        }

        try
        {
            GetImagePixels(path, out int width, out int height, out _);
            if (_imagePaths.Count > 0 && (width != _width || height != _height))
            {
                EditorUtility.DisplayDialog(
                    "Image Dimensions Do Not Match",
                    $"The selected image is {width}x{height}, but the current images are {_width}x{_height}.",
                    "OK");
                return;
            }

            if (_imagePaths.Contains(path))
            {
                EditorUtility.DisplayDialog("Image Already Added", "That PNG is already in the input list.", "OK");
                return;
            }

            _width = width;
            _height = height;
            _imagePaths.Add(path);
            _sampleCounts.Add(string.Empty);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Could Not Read Image", exception.Message, "OK");
        }
    }

    private void AverageImages()
    {
        string outputPath = EditorUtility.SaveFilePanel(
            "Save Averaged PNG",
            string.Empty,
            "averaged-image",
            "png");
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        try
        {
            var sums = new Color[_width * _height];
            double totalWeight = 0.0;
            for (int imageIndex = 0; imageIndex < _imagePaths.Count; imageIndex++)
            {
                string imagePath = _imagePaths[imageIndex];
                GetImagePixels(imagePath, out int width, out int height, out Color[] pixels);
                if (width != _width || height != _height || pixels.Length != sums.Length)
                {
                    throw new InvalidOperationException($"Image dimensions changed while reading '{imagePath}'.");
                }

                double weight = GetWeight(_sampleCounts[imageIndex], imagePath);
                totalWeight += weight;
                float pixelWeight = (float)weight;

                for (int pixelIndex = 0; pixelIndex < sums.Length; pixelIndex++)
                {
                    sums[pixelIndex] += pixels[pixelIndex] * pixelWeight;
                }
            }

            float divisor = (float)totalWeight;
            var output = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            try
            {
                for (int pixelIndex = 0; pixelIndex < sums.Length; pixelIndex++)
                {
                    output.SetPixel(pixelIndex % _width, pixelIndex / _width, sums[pixelIndex] / divisor);
                }
                output.Apply(false, false);
                string outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }
                File.WriteAllBytes(outputPath, output.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(output);
            }

            Debug.Log($"Averaged {_imagePaths.Count} PNG images into '{outputPath}'.");
            EditorUtility.RevealInFinder(outputPath);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Could Not Average Images", exception.Message, "OK");
        }
    }

    private static double GetWeight(string sampleCount, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(sampleCount))
        {
            return 1.0;
        }

        if (!double.TryParse(sampleCount, out double weight) || double.IsNaN(weight)
            || double.IsInfinity(weight) || weight <= 0.0)
        {
            throw new InvalidOperationException(
                $"The sample count for '{Path.GetFileName(imagePath)}' must be blank or a positive number.");
        }

        return weight;
    }

    private static void GetImagePixels(string path, out int width, out int height, out Color[] pixels)
    {
        var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (!image.LoadImage(bytes, false))
            {
                throw new InvalidOperationException($"'{path}' is not a readable PNG image.");
            }

            width = image.width;
            height = image.height;
            pixels = image.GetPixels();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(image);
        }
    }
}
