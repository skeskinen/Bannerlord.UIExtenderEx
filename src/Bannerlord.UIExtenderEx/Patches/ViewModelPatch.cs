﻿using HarmonyLib;
using HarmonyLib.BUTR.Extensions;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using TaleWorlds.Library;

namespace Bannerlord.UIExtenderEx.Patches
{
    internal static class ViewModelPatch
    {
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(ViewModel), nameof(ViewModel.ExecuteCommand)),
                transpiler: new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => ViewModel_ExecuteCommand_Transpiler(null!, null!))));
        }

        [SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "For ReSharper")]
        [SuppressMessage("ReSharper", "UnusedMethodReturnValue.Local")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<CodeInstruction> ViewModel_ExecuteCommand_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilGenerator)
        {
            var instructionList = instructions.ToList();

            var jmpOriginalFlow = ilGenerator.DefineLabel();
            instructionList[0].labels.Add(jmpOriginalFlow);

            instructionList.InsertRange(0, new List<CodeInstruction>
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldarg_1),
                new(OpCodes.Ldarg_2),
                new(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ExecuteCommand(null!, null!, null!))),
                new(OpCodes.Brtrue, jmpOriginalFlow),
                new(OpCodes.Ret)
            });
            return instructionList;
        }
        [SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "For ReSharper")]
        [SuppressMessage("ReSharper", "UnusedMethodReturnValue.Local")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ExecuteCommand(ViewModel viewModel, string commandName, params object[] parameters)
        {
            static object? ConvertValueTo(string value, Type parameterType)
            {
                if (parameterType == typeof(string))
                    return value;
                if (parameterType == typeof(int))
                    return Convert.ToInt32(value);
                if (parameterType == typeof(float))
                    return Convert.ToSingle(value);
                return null;
            }

            foreach (var runtime in UIExtender.GetAllRuntimes())
            {
                if (!runtime.ViewModelComponent.Enabled)
                    continue;

                var nativeMethod = AccessTools2.Method(viewModel.GetType(), commandName);
                var isNativeMethod = nativeMethod is not null;
                var hasMixins = runtime.ViewModelComponent.MixinInstanceCache.TryGetValue(viewModel, out var list);

                if (!isNativeMethod && !hasMixins)
                    return false; // stop original command execution
                if (isNativeMethod && !hasMixins)
                    continue; // skip to next Runtime

                foreach (var mixin in list)
                {
                    if (!runtime.ViewModelComponent.MixinInstanceMethodCache.TryGetValue(mixin, out var methodExtensions))
                        continue;

                    if (!(methodExtensions.FirstOrDefault(e => e.Key == commandName).Value is { } method))
                        continue;

                    if (method.GetParameters() is { } methodParameters && methodParameters.Length == parameters.Length)
                    {
                        var array = new object?[parameters.Length];
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            var methodParameterType = methodParameters[i].ParameterType;

                            var obj = parameters[i];
                            array[i] = obj;
                            if (obj is string str && methodParameterType != typeof(string))
                            {
                                array[i] = ConvertValueTo(str, methodParameterType);
                            }
                        }

                        method.InvokeWithLog(viewModel, array);
                        return false;
                    }

                    if (method.GetParameters().Length == 0)
                    {
                        method.InvokeWithLog(viewModel, null);
                        return false;
                    }
                }
            }

            // continue original execution
            return true;
        }
    }
}