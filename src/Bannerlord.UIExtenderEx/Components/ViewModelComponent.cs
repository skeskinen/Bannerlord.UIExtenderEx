﻿using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Extensions;
using Bannerlord.UIExtenderEx.Patches;
using Bannerlord.UIExtenderEx.ViewModels;

using HarmonyLib;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using TaleWorlds.Library;

namespace Bannerlord.UIExtenderEx.Components
{
    /// <summary>
    /// Component that deals with extended VM generation and runtime support
    /// </summary>
    internal class ViewModelComponent
    {
        [SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "For ReSharper")]
        [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")]
        private readonly string _moduleName;
        private readonly Harmony _harmony;

        /// <summary>
        /// List of registered mixin types
        /// </summary>
        public readonly ConcurrentDictionary<Type, List<Type>> Mixins = new();

        /// <summary>
        /// Cache of mixin instances. Key is generated by `mixinCacheKey`. Instances are removed when original view model is deallocated
        /// </summary>
        internal readonly ConditionalWeakTable<ViewModel, List<IViewModelMixin>> MixinInstanceCache = new();
        internal readonly ConditionalWeakTable<ViewModel, object> MixinInstanceRefreshFromConstructorCache = new();

        internal readonly ConditionalWeakTable<IViewModelMixin, Dictionary<string, PropertyInfo>> MixinInstancePropertyCache = new();

        internal readonly ConditionalWeakTable<IViewModelMixin, Dictionary<string, MethodInfo>> MixinInstanceMethodCache = new();

        public bool Enabled { get; private set; }

        public ViewModelComponent(string moduleName)
        {
            _moduleName = moduleName;
            _harmony = new Harmony($"bannerlord.uiextender.ex.viewmodels.{_moduleName}");
        }

        public void Enable()
        {
            Enabled = true;
        }
        public void Disable()
        {
            Enabled = false;
        }

        /// <summary>
        /// Register mixin type.
        /// </summary>
        /// <param name="mixinType">mixin type, should be a subclass of ViewModelExtender<T> where T specify view model to extend</param>
        /// <param name="refreshMethodName"></param>
        public void RegisterViewModelMixin(Type mixinType, string? refreshMethodName = null)
        {
            var viewModelType = GetViewModelType(mixinType);
            if (viewModelType is null)
            {
                Utils.Fail($"Failed to find base type for mixin {mixinType}, should be specialized as T of ViewModelMixin<T>!");
                return;
            }

            Mixins.GetOrAdd(viewModelType, _ => new List<Type>()).Add(mixinType);

            ViewModelWithMixinPatch.Patch(_harmony, viewModelType, Mixins.Keys, refreshMethodName);
        }

        /// <summary>
        /// Initialize mixin instances for specified view model instance, called in extended VM constructor.
        /// </summary>
        /// <param name="baseType">base type of VM (as found in game)</param>
        /// <param name="instance">instance of extended VM</param>
        public void InitializeMixinsForVMInstance(Type baseType, ViewModel instance)
        {
            var mixins = MixinInstanceCache.GetOrAdd(instance, _ => new List<IViewModelMixin>());

            if (!Mixins.ContainsKey(baseType))
                return;

            var newMixins = Mixins[baseType]
                .Where(mixinType => mixins.All(mixin => mixin.GetType() != mixinType))
                .Select(mixinType => Activator.CreateInstance(mixinType, instance) as IViewModelMixin)
                .Where(mixin => mixin is not null)
                .Cast<IViewModelMixin>()
                .ToList();

            mixins.AddRange(newMixins);

            foreach (var viewModelMixin in newMixins)
            {
                var propertyCache = MixinInstancePropertyCache.GetOrAdd(viewModelMixin, _ => new Dictionary<string, PropertyInfo>());
                var methodCache = MixinInstanceMethodCache.GetOrAdd(viewModelMixin, _ => new Dictionary<string, MethodInfo>());

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

        private static Type? GetViewModelType(Type mixinType)
        {
            Type? viewModelType = null;
            var node = mixinType;
            while (node is not null)
            {
                if (typeof(IViewModelMixin).IsAssignableFrom(node))
                {
                    viewModelType = node.GetGenericArguments().FirstOrDefault();
                    if (viewModelType is not null)
                    {
                        break;
                    }
                }

                node = node.BaseType;
            }

            return viewModelType;
        }
    }
}