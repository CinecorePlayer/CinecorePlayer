using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecoreMac;

// Dimensionamento dell'immagine a schermo intero (Utilities/ImageSizing.cs di Windows, senza System.Drawing):
// stessi conti, stesso file di impostazioni (image-sizing.json), stesso stabilizzatore del formato.

internal enum ImageSizingMode { Fill, ConstantHeight, ConstantArea, Custom }

internal sealed class ImageSizingSettings
{
    /// <summary>Formati di riferimento e moltiplicatore predefinito della modalita' personalizzata (0 = tutta l'area).</summary>
    public static readonly (string Key, double Aspect, double Default)[] Presets =
    {
        ("1.33", 4 / 3.0, 1.00), ("1.43", 1.43, 0), ("1.78", 16 / 9.0, 1.00), ("1.85", 1.85, 1.04), ("2.00", 2.00, 1.06), ("2.39", 2.39, 1.10),
    };

    public ImageSizingMode Mode { get; set; } = ImageSizingMode.Fill;
    /// <summary>Personalizzata: 0 = altezza costante, 1 = area costante.</summary>
    public double Bias { get; set; } = 0;
    public Dictionary<string, double> Multipliers { get; set; } = new();
    public int TransitionMs { get; set; } = 400;
    public bool DetectBars { get; set; } = true;
    public bool ExpandTallScenes { get; set; } = true;

    public double Multiplier(string key) => Multipliers.TryGetValue(key, out double value) ? value : Presets.First(p => p.Key == key).Default;

    private static string FilePath => Path.Combine(LibraryStore.AppDataDir, "image-sizing.json");

    public static ImageSizingSettings Load()
    {
        try
        {
            if (JsonSerializer.Deserialize<ImageSizingSettings>(File.ReadAllText(FilePath)) is { } loaded)
            {
                loaded.Bias = Math.Clamp(loaded.Bias, 0, 1);
                loaded.TransitionMs = Math.Clamp(loaded.TransitionMs, 0, 3000);
                loaded.Multipliers ??= new();
                return loaded;
            }
        }
        catch { }
        return new ImageSizingSettings();
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { Console.Error.WriteLine("[SIZING] save: " + ex.Message); }
    }

    public static string ModeName(ImageSizingMode mode) => mode switch
    {
        ImageSizingMode.ConstantHeight => "Altezza costante",
        ImageSizingMode.ConstantArea => "Area costante",
        ImageSizingMode.Custom => "Personalizzata",
        _ => "Riempi",
    };
}

internal static class ImageSizing
{
    private const double ReferenceAspect = 16 / 9.0;

    public readonly record struct Box(double X, double Y, double Width, double Height) { public bool IsEmpty => Width <= 0 || Height <= 0; }

    public static Box Fit(double screenW, double screenH, double aspect)
    {
        if (screenW <= 0 || screenH <= 0 || !(aspect > 0)) return default;
        double width = screenW, height = width / aspect;
        if (height > screenH) { height = screenH; width = height * aspect; }
        return new((screenW - width) / 2, (screenH - height) / 2, width, height);
    }

    public static Box Compute(double screenW, double screenH, double aspect, ImageSizingSettings settings, bool expanded = false)
    {
        var fit = Fit(screenW, screenH, aspect);
        if (fit.IsEmpty || settings.Mode == ImageSizingMode.Fill || (expanded && settings.ExpandTallScenes)) return fit;
        double relative = RelativeHeight(aspect, settings);
        if (relative <= 0) return fit;
        double height = Math.Min(relative * BaseHeight(screenW, screenH, settings), screenH), width = height * aspect;
        if (width > screenW) { width = screenW; height = width / aspect; }
        return new((screenW - width) / 2, (screenH - height) / 2, width, height);
    }

    public static double RelativeHeight(double aspect, ImageSizingSettings settings)
    {
        double area = Math.Sqrt(ReferenceAspect / aspect);
        return settings.Mode switch
        {
            ImageSizingMode.ConstantHeight => 1,
            ImageSizingMode.ConstantArea => area,
            ImageSizingMode.Custom => ((1 - settings.Bias) + settings.Bias * area) * Multiplier(aspect, settings),
            _ => 0
        };
    }

    // La misura comune: la piu' grande per cui tutti i formati di riferimento stanno nello schermo.
    public static double BaseHeight(double screenW, double screenH, ImageSizingSettings settings)
    {
        double best = double.MaxValue;
        foreach (var preset in ImageSizingSettings.Presets)
        {
            double relative = RelativeHeight(preset.Aspect, settings);
            if (relative <= 0) continue;
            best = Math.Min(best, Math.Min(screenH / relative, screenW / (preset.Aspect * relative)));
        }
        return best == double.MaxValue ? screenH : best;
    }

    private static double Multiplier(double aspect, ImageSizingSettings settings)
    {
        var presets = ImageSizingSettings.Presets;
        foreach (var preset in presets)
            if (Math.Abs(aspect / preset.Aspect - 1) <= 0.03) return settings.Multiplier(preset.Key);
        var sized = presets.Where(p => settings.Multiplier(p.Key) > 0).ToArray();
        if (sized.Length == 0) return 0;
        if (aspect <= sized[0].Aspect) return settings.Multiplier(sized[0].Key);
        if (aspect >= sized[^1].Aspect) return settings.Multiplier(sized[^1].Key);
        for (int i = 1; i < sized.Length; i++)
        {
            if (aspect > sized[i].Aspect) continue;
            double t = Math.Log(aspect / sized[i - 1].Aspect) / Math.Log(sized[i].Aspect / sized[i - 1].Aspect);
            return settings.Multiplier(sized[i - 1].Key) * (1 - t) + settings.Multiplier(sized[i].Key) * t;
        }
        return 1;
    }

    /// <summary>Scala del fotogramma intero (bande comprese) perche' la parte attiva occupi <paramref name="active"/>.</summary>
    public static double FrameScale(double screenW, double screenH, double frameAspect, double activeAspect, Box active)
    {
        var fit = Fit(screenW, screenH, frameAspect);
        if (fit.IsEmpty || active.IsEmpty) return 1;
        double frameHeight = activeAspect >= frameAspect ? active.Width / frameAspect : active.Height;
        return frameHeight / fit.Height;
    }

    private static readonly double[] KnownAspects = { 1.333, 1.375, 1.43, 1.66, 1.778, 1.85, 1.90, 2.00, 2.20, 2.35, 2.39, 2.55, 2.76 };

    public static double Snap(double aspect)
    {
        double nearest = KnownAspects.OrderBy(known => Math.Abs(known - aspect)).First();
        if (nearest is 2.35) nearest = 2.39;
        return Math.Abs(aspect / nearest - 1) <= 0.02 ? nearest : aspect;
    }

    /// <summary>Formato della parte non nera di un fotogramma in toni di grigio (null: scena buia, bande asimmetriche…).</summary>
    public static double? DetectActiveAspect(byte[] luma, int width, int height, double frameAspect)
    {
        if (width < 32 || height < 32 || luma.Length < width * height || !(frameAspect > 0)) return null;
        var rows = new bool[height];
        var columns = new int[width];
        for (int y = 0; y < height; y++)
        {
            int bright = 0, row = y * width;
            for (int x = 0; x < width; x++)
                if (luma[row + x] > 24) { bright++; columns[x]++; }
            rows[y] = bright > width / 50;
        }
        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < height && !rows[top]) top++;
        if (top >= height) return null;
        while (!rows[height - 1 - bottom]) bottom++;
        while (left < width && columns[left] <= height / 50) left++;
        if (left >= width) return null;
        while (columns[width - 1 - right] <= height / 50) right++;
        int activeHeight = height - top - bottom, activeWidth = width - left - right;
        if (activeHeight < height * 0.35 || activeWidth < width * 0.45) return null;
        if (Math.Abs(top - bottom) > Math.Max(2, height * 0.025) || Math.Abs(left - right) > Math.Max(2, width * 0.025)) return null;
        if (top + bottom < height * 0.03) activeHeight = height;
        if (left + right < width * 0.03) activeWidth = width;
        return Snap(frameAspect * (activeWidth / (double)width) / (activeHeight / (double)height));
    }
}

/// <summary>Un formato nuovo vale dopo due misure concordi; tiene il formato prevalente per riconoscere le scene IMAX.</summary>
internal sealed class AspectStabilizer
{
    public const int SamplesToKnowTheFilm = 30;
    private readonly Dictionary<int, int> _time = new();
    private double _candidate;
    private int _agreeing;

    public double Current { get; private set; }
    public double Usual { get; private set; }
    public bool Expanded => _time.TryGetValue(Key(Usual), out int samples) && samples >= SamplesToKnowTheFilm && Current < Usual * 0.93;

    public AspectStabilizer(double initial) { Current = Usual = initial; }

    private static int Key(double aspect) => (int)Math.Round(aspect * 100);

    public bool Offer(double? sample)
    {
        bool changed = false;
        if (sample is double aspect && aspect > 0)
        {
            if (Math.Abs(aspect / Current - 1) < 0.03) _agreeing = 0;
            else
            {
                if (_agreeing > 0 && Math.Abs(aspect / _candidate - 1) < 0.02) _agreeing++;
                else { _candidate = aspect; _agreeing = 1; }
                if (_agreeing >= 2) { Current = _candidate; _agreeing = 0; changed = true; }
            }
        }
        int key = Key(Current);
        _time[key] = _time.GetValueOrDefault(key) + 1;
        if (_time[key] > _time.GetValueOrDefault(Key(Usual))) Usual = Current;
        return changed;
    }
}
