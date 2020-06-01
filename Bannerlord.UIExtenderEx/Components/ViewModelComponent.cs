﻿using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.ViewModels;

using HarmonyLib;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using TaleWorlds.Library;

namespace Bannerlord.UIExtenderEx.Components
{
    /// <summary>
    /// Component that deals with extended VM generation and runtime support
    /// </summary>
    internal class ViewModelComponent
    {
        private readonly string _moduleName;
        private readonly Harmony _harmony;

        /// <summary>
        /// List of registered mixin types
        /// </summary>
        private readonly Dictionary<Type, List<Type>> _mixins = new Dictionary<Type, List<Type>>();

        /// <summary>
        /// Cache of mixin instances. Key is generated by `mixinCacheKey`. Instances are removed when original view model is deallocated
        /// </summary>
        private readonly Dictionary<string, List<IViewModelMixin>> _mixinInstanceCache = new Dictionary<string, List<IViewModelMixin>>();

        private readonly Dictionary<IViewModelMixin, Dictionary<string, PropertyInfo>> _mixinInstancePropertyCache = new Dictionary<IViewModelMixin, Dictionary<string, PropertyInfo>>();

        private readonly Dictionary<IViewModelMixin, Dictionary<string, MethodInfo>> _mixinInstanceMethodCache = new Dictionary<IViewModelMixin, Dictionary<string, MethodInfo>>();

        public bool Enabled { get; private set; }

        public ViewModelComponent(string moduleName)
        {
            _moduleName = moduleName;
            _harmony = new Harmony($"bannerlord.uiextender.ex.viewmodels.{_moduleName}");
        }

        /// <summary>
        /// Register mixin type.
        /// </summary>
        /// <param name="mixinType">mixin type, should be a subclass of ViewModelExtender<T> where T specify view model to extend</param>
        public void RegisterViewModelMixin(Type mixinType, string? refreshMethodName = null)
        {
            var viewModelType = GetViewModelType(mixinType);

            Utils.CompatAssert(viewModelType != null, $"Failed to find base type for mixin {mixinType}, should be specialized as T of ViewModelMixin<T>!");
            if (viewModelType == null)
                return;

            _mixins.Get(viewModelType, () => new List<Type>()).Add(mixinType);


            var constructorMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_Constructor_Transpiler));
            var executeCommandMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_ExecuteCommand_Transpiler));
            var refreshMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_Refresh_Transpiler));
            var finalizeMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_Finalize_Transpiler));

            foreach (var constructor in _mixins.Keys.SelectMany(m => m.GetConstructors()))
            {
                _harmony.Patch(
                    constructor,
                    transpiler: new HarmonyMethod(new WrappedMethodInfo(constructorMethod, this)));
            }

            _harmony.Patch(
                AccessTools.DeclaredMethod(typeof(ViewModel), nameof(ViewModel.ExecuteCommand)),
                transpiler: new HarmonyMethod(new WrappedMethodInfo(executeCommandMethod, this)));

            if (refreshMethodName != null && AccessTools.Method(viewModelType, refreshMethodName) is { } method)
            {
                _harmony.Patch(
                    method,
                    transpiler: new HarmonyMethod(new WrappedMethodInfo(refreshMethod, this)));
            }

            // TODO: recursion
            _harmony.Patch(
                AccessTools.DeclaredMethod(viewModelType, nameof(ViewModel.OnFinalize)) ??
                AccessTools.DeclaredMethod(typeof(ViewModel), nameof(ViewModel.OnFinalize)),
                transpiler: new HarmonyMethod(new WrappedMethodInfo(finalizeMethod, this)));
        }

        public void Enable()
        {
            Enabled = true;

            /*
            var constructorMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_Constructor_Transpiler));
            var executeCommandMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_ExecuteCommand_Transpiler));

            foreach (var constructor in _mixins.Keys.SelectMany(m => m.GetConstructors()))
            {
                _harmony.Patch(
                    constructor,
                    transpiler: new HarmonyMethod(new WrappedMethodInfo(constructorMethod, this)));
            }

            _harmony.Patch(
                AccessTools.Method(typeof(ViewModel), nameof(ViewModel.ExecuteCommand)),
                transpiler: new HarmonyMethod(new WrappedMethodInfo(executeCommandMethod, this)));
            */
        }
        public void Disable()
        {
            Enabled = false;

            // Not working with Wrapped MethodInfo
            /*
            var constructorMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_Constructor_Transpiler));
            var executeCommandMethod = AccessTools.Method(typeof(ViewModelComponent), nameof(ViewModel_ExecuteCommand_Transpiler));

            foreach (var constructor in _mixins.Keys.SelectMany(viewModelType => viewModelType.GetConstructors()))
            {
                _harmony.Unpatch(constructor, new WrappedMethodInfo(constructorMethod, this));
            }

            _harmony.Unpatch(AccessTools.Method(typeof(ViewModel), nameof(ViewModel.ExecuteCommand)), new WrappedMethodInfo(executeCommandMethod, this));
            */
        }

        /// <summary>
        /// Initialize mixin instances for specified view model instance, called in extended VM constructor.
        /// </summary>
        /// <param name="baseType">base type of VM (as found in game)</param>
        /// <param name="instance">instance of extended VM</param>
        private void InitializeMixinsForVMInstance(Type baseType, object instance)
        {
            var list = MixinCacheList(instance);
            foreach (var mixinType in _mixins[baseType])
            {
                list.Add((IViewModelMixin) Activator.CreateInstance(mixinType, instance));
            }

            foreach (var viewModelMixin in list)
            {
                var propertyCache = _mixinInstancePropertyCache.Get(viewModelMixin, () => new Dictionary<string, PropertyInfo>());
                var methodCache = _mixinInstanceMethodCache.Get(viewModelMixin, () => new Dictionary<string, MethodInfo>());

                foreach (var property in viewModelMixin.GetType().GetProperties().Where(p => p.CustomAttributes.Any(a => a.AttributeType == typeof(DataSourceProperty))))
                {
                    propertyCache.Add(property.Name, new WrappedPropertyInfo(property, viewModelMixin));
                }
                foreach (var method in viewModelMixin.GetType().GetMethods().Where(p => p.CustomAttributes.Any(a => a.AttributeType == typeof(DataSourceMethodAttribute))))
                {
                    methodCache.Add(method.Name, new WrappedMethodInfo(method, viewModelMixin));
                }
            }
        }


        /// <summary>
        /// Get list of mixin instances from _mixinInstanceCache associated with VM instance
        /// </summary>
        /// <param name="instance"></param>
        /// <returns></returns>
        private List<IViewModelMixin> MixinCacheList(object instance) => _mixinInstanceCache.Get(MixinCacheKey(instance), () => new List<IViewModelMixin>());

        /// <summary>
        /// Construct string key for _mixinInstanceCache
        /// </summary>
        /// <param name="instance"></param>
        /// <returns></returns>
        private static string MixinCacheKey(object instance) => $"{instance.GetType()}_{instance.GetHashCode()}";

        private static Type? GetViewModelType(Type mixinType)
        {
            Type? viewModelType = null;
            var node = mixinType;
            while (node != null)
            {
                if (typeof(IViewModelMixin).IsAssignableFrom(node))
                {
                    viewModelType = node.GetGenericArguments().FirstOrDefault();
                    if (viewModelType != null)
                    {
                        break;
                    }
                }

                node = node.BaseType;
            }

            return viewModelType;
        }


        private IEnumerable<CodeInstruction> ViewModel_Constructor_Transpiler(IEnumerable<CodeInstruction> instructions) =>
            InsertMethodAtEnd(instructions, AccessTools.DeclaredMethod(typeof(ViewModelComponent), nameof(Constructor)));
        private static void Constructor(string moduleName, ViewModel viewModel)
        {
            if (!(UIExtender.RuntimeFor(moduleName) is { } runtime) || !runtime.ViewModelComponent.Enabled)
                return;

            runtime.ViewModelComponent.InitializeMixinsForVMInstance(viewModel.GetType(), viewModel);

            if (!runtime.ViewModelComponent._mixinInstanceCache.TryGetValue(MixinCacheKey(viewModel), out var list))
                return;

            foreach (var mixin in list)
            foreach (var extension in runtime.ViewModelComponent._mixinInstancePropertyCache[mixin])
            {
                viewModel.AddProperty(extension.Key, extension.Value);
            }
        }

        private IEnumerable<CodeInstruction> ViewModel_ExecuteCommand_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilGenerator)
        {
            var first = true;
            foreach (var instruction in instructions)
            {
                if (first)
                {
                    var label = ilGenerator.DefineLabel();
                    instruction.labels.Add(label);
                    yield return new CodeInstruction(OpCodes.Ldstr, _moduleName);
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ViewModelComponent), nameof(ExecuteCommand)));
                    yield return new CodeInstruction(OpCodes.Brtrue, label);
                    yield return new CodeInstruction(OpCodes.Ret);
                    yield return instruction;
                    first = false;
                    continue;
                }

                yield return instruction;
            }
        }
        private static bool ExecuteCommand(string moduleName, ViewModel viewModel, string commandName, object[] parameters)
        {
            static object? ConvertValueTo(string value, Type parameterType)
            {
                object? result = null;
                if (parameterType == typeof(string))
                    result = value;
                else if (parameterType == typeof(int))
                    result = Convert.ToInt32(value);
                else if (parameterType == typeof(float))
                    result = Convert.ToSingle(value);
                return result;
            }

            if (!(UIExtender.RuntimeFor(moduleName) is { } runtime) || !runtime.ViewModelComponent.Enabled)
                return true;

            var nativeMethod = viewModel.GetType().GetMethod(commandName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var isNativeMethod = nativeMethod != null;
            var hasMixins = runtime.ViewModelComponent._mixinInstanceCache.TryGetValue(MixinCacheKey(viewModel), out var list);

            if (!isNativeMethod && !hasMixins)
                return false; // stop original execution
            if (isNativeMethod && !hasMixins)
                return true; // continue original execution

            foreach (var mixin in list)
            {
                if (!(runtime.ViewModelComponent._mixinInstanceMethodCache[mixin].FirstOrDefault(e => e.Key == commandName).Value is { } method))
                    continue;

                if (method.GetParameters() is { } methodParameters && methodParameters.Length == parameters.Length)
                {
                    var array = new object[parameters.Length];
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
                    //method.Invoke(viewModel, array);
                    return false;
                }

                if (method.GetParameters().Length == 0)
                {
                    method.InvokeWithLog(viewModel, null);
                    return false;
                }
            }

            return true;
        }

        private IEnumerable<CodeInstruction> ViewModel_Refresh_Transpiler(IEnumerable<CodeInstruction> instructions) =>
            InsertMethodAtEnd(instructions, AccessTools.DeclaredMethod(typeof(ViewModelComponent), nameof(Refresh)));
        private static void Refresh(string moduleName, ViewModel viewModel)
        {
            if (!(UIExtender.RuntimeFor(moduleName) is { } runtime) || !runtime.ViewModelComponent.Enabled ||
                !runtime.ViewModelComponent._mixinInstanceCache.TryGetValue(MixinCacheKey(viewModel), out var list)
            )
                return;

            foreach (var mixin in list)
                mixin.OnRefresh();
        }

        private IEnumerable<CodeInstruction> ViewModel_Finalize_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    var labels = instruction.labels;
                    instruction.labels = new List<Label>();
                    yield return new CodeInstruction(OpCodes.Ldstr, _moduleName) { labels = labels };
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ViewModelComponent), nameof(Finalize)));
                }

                yield return instruction;
            }
        }
        private static void Finalize(string moduleName, ViewModel viewModel)
        {
            if (!(UIExtender.RuntimeFor(moduleName) is { } runtime) || !runtime.ViewModelComponent.Enabled ||
                !runtime.ViewModelComponent._mixinInstanceCache.TryGetValue(MixinCacheKey(viewModel), out var list))
                return;

            foreach (var mixin in list)
                mixin.OnFinalize();
        }

        private IEnumerable<CodeInstruction> InsertMethodAtEnd(IEnumerable<CodeInstruction> instructions, MethodInfo method, params CodeInstruction[] paramCodeInstructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    var labels = instruction.labels;
                    instruction.labels = new List<Label>();
                    yield return new CodeInstruction(OpCodes.Ldstr, _moduleName) { labels = labels };
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    foreach (var paramCodeInstruction in paramCodeInstructions)
                        yield return paramCodeInstruction;
                    yield return new CodeInstruction(OpCodes.Call, method);
                }

                yield return instruction;
            }
        }
    }
}