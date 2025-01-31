﻿using HarmonyLib;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using TaleWorlds.GauntletUI.Data;

namespace Bannerlord.UIExtenderEx.Patches
{
    internal static class GauntletMoviePatch
    {
        private static readonly ConcurrentDictionary<UIExtenderRuntime, List<string>> WidgetNames = new();

        public static void Register(UIExtenderRuntime runtime, string? autoGenWidgetName)
        {
            if (string.IsNullOrEmpty(autoGenWidgetName))
                return;

            WidgetNames.AddOrUpdate(runtime,
                _ => new List<string> { autoGenWidgetName! },
                (_, list) =>
                {
                    list.Add(autoGenWidgetName!);
                    return list;
                });
        }

        public static void Patch(Harmony harmony)
        {
            if (AccessTools.Method(typeof(GauntletMovie), "Load") is { } methodInfo &&
                methodInfo.GetParameters() is { } @params &&
                @params.Any(p => p.Name == "doNotUseGeneratedPrefabs"))
            {
                harmony.Patch(
                    methodInfo,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GauntletMoviePatch), nameof(LoadPrefix))));
            }
        }

        private static void LoadPrefix(string movieName, ref bool doNotUseGeneratedPrefabs)
        {
            var movies = WidgetNames.Where(kv => kv.Key.PrefabComponent.Enabled).SelectMany(kv => kv.Value);
            if (movies.Contains(movieName))
                doNotUseGeneratedPrefabs = true;
        }
    }
}