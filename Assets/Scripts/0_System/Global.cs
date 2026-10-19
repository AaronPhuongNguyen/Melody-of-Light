using System;
using UnityEngine;

public static class RNG
{
    private static Unity.Mathematics.Random _state;
    private static uint _seed;

    static RNG() => SetSeed((uint)System.DateTime.Now.Ticks);

    public static void SetSeed(uint seed)
    {
        _seed = seed == 0 ? 1u : seed;
        _state = new Unity.Mathematics.Random(_seed);
    }

    public static void Reset() => _state = new Unity.Mathematics.Random(_seed);
    public static uint Seed => _seed;

    public static int GetInt(int min, int max) => _state.NextInt(min, max);
    public static float GetFloat(float min, float max) => _state.NextFloat(min, max);
    public static float GetPercent() => GetInt(0, 4097) / 4096f; // [0, 1);
    public static Vector2 GetVector2(float min, float max) => _state.NextFloat2(min, max);
    public static Vector2 GetUniformVector2(float min, float max)
    {
        float v = GetFloat(min, max);
        return new Vector2(v, v);
    }
    public static Vector2 GetInsideCircle(float radius)
    {
        float angle = GetFloat(0, Mathf.PI * 2f);
        float dist = GetFloat(0, radius);
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
    }
}
public static class EventBus
{
    public static Action OnLanguageChanged;

    public static void RaiseLanguageChanged() => OnLanguageChanged?.Invoke();
}