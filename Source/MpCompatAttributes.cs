﻿using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class MpCompatForAttribute : Attribute
    {
        public string PackageId { get; }

        public MpCompatForAttribute(string packageId)
        {
            this.PackageId = packageId;
        }

        public override object TypeId => this;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class MpCompatRequireModAttribute : Attribute
    {
        public string PackageId { get; }

        public MpCompatRequireModAttribute(string packageId) => PackageId = packageId;

        public override object TypeId => this;
    }

    public static class MpCompatPatchLoader
    {
        private static readonly MethodInfo RegisterSyncWorker;

        static MpCompatPatchLoader()
        {
            RegisterSyncWorker = 
                typeof(MP)
                    .GetMethods(AccessTools.allDeclared)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != nameof(MP.RegisterSyncWorker))
                            return false;
                        if (!m.IsGenericMethod)
                            return false;

                        var parms = m.GetParameters();
                        if (parms is not { Length: 4 })
                            return false;

                        return parms[0].ParameterType.GetGenericTypeDefinition() == typeof(SyncWorkerDelegate<>) &&
                               parms[1].ParameterType == typeof(Type) &&
                               parms[2].ParameterType == typeof(bool) &&
                               parms[3].ParameterType == typeof(bool);
                    });

            if (RegisterSyncWorker == null)
                Log.Error($"Failed retrieving {nameof(MP.RegisterSyncWorker)}");
        }

        public static void LoadPatch(object instance) => LoadPatch(instance?.GetType());

        public static void LoadPatch<T>() => LoadPatch(typeof(T));

        public static void LoadPatch(Type type)
        {
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.IsStatic))
            {
                foreach (var attr in Attribute.GetCustomAttributes(method))
                {
                    try
                    {
                        switch (attr)
                        {
                            case MpCompatPatchAttribute mpPatch:
                            {
                                var patch = new HarmonyMethod(method);
                                MpCompat.harmony.Patch(mpPatch.Method,
                                    mpPatch is MpCompatPrefixAttribute ? patch : null,
                                    mpPatch is MpCompatPostfixAttribute ? patch : null,
                                    mpPatch is MpCompatTranspilerAttribute ? patch : null,
                                    mpPatch is MpCompatFinalizerAttribute ? patch : null);
                                break;
                            }
                            case MpCompatSyncMethodAttribute syncMethod:
                            {
                                var sync = MP.RegisterSyncMethod(method).SetContext(syncMethod.context);

                                if (syncMethod.cancelIfAnyArgNull)
                                    sync.CancelIfAnyArgNull();
                                if (syncMethod.cancelIfNoSelectedMapObjects)
                                    sync.CancelIfNoSelectedMapObjects();
                                if (syncMethod.cancelIfNoSelectedWorldObjects)
                                    sync.CancelIfNoSelectedWorldObjects();
                                if (syncMethod.debugOnly)
                                    sync.SetDebugOnly();
                                if (syncMethod.exposeParameters != null)
                                {
                                    foreach (var index in syncMethod.exposeParameters)
                                        sync.ExposeParameter(index);
                                }

                                break;
                            }
                            case MpCompatSyncWorkerAttribute syncWorker:
                            {
                                if (RegisterSyncWorker == null)
                                    continue;
                                if (method.ReturnType != typeof(void))
                                    throw new Exception("Cannot register sync worker: return type must be null.");
                                var parms = method.GetParameters();
                                if (parms is not { Length: 2 })
                                    throw new Exception("Cannot register sync worker: delegate doesn't have 2 parameters");
                                if (parms[0].ParameterType != typeof(SyncWorker))
                                    throw new Exception($"Cannot register sync worker: the first argument isn't of type {nameof(SyncWorker)}");

                                // The type is normally a ref type, get one that isn't
                                var genericNonRefType = parms[1].ParameterType.GetElementType();
                                var delType = typeof(SyncWorkerDelegate<>).MakeGenericType(genericNonRefType);
                                var del = Delegate.CreateDelegate(delType, method);

                                RegisterSyncWorker.MakeGenericMethod(genericNonRefType)
                                    .Invoke(null, new object[] { del, syncWorker.Type, syncWorker.isImplicit, syncWorker.shouldConstruct });

                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"MpCompatPatch {method.DeclaringType}.{method.Name} failed with exception: {e}");
                    }
                }
            }

            foreach (var field in AccessTools.GetDeclaredFields(type).Where(f => f.IsStatic && typeof(ISyncField).IsAssignableFrom(f.FieldType)))
            {
                try
                {
                    if (field.TryGetAttribute<MpCompatSyncFieldAttribute>(out var attribute))
                    {
                        var sync = MP.RegisterSyncField(attribute.Field);

                        // It seems Context is unused in MP
                        if (attribute.cancelIfValueNull)
                            sync.CancelIfValueNull();
                        if (attribute.inGameLoop)
                            sync.InGameLoop();
                        if (attribute.bufferChanges)
                            sync.SetBufferChanges();
                        if (attribute.debugOnly)
                            sync.SetDebugOnly();
                        if (attribute.hostOnly)
                            sync.SetHostOnly();
                        if (attribute.version > 0)
                            sync.SetVersion(attribute.version);

                        field.SetValue(null, sync);
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"MpCompatPatch {field.DeclaringType}.{field.Name} failed with exception: {e}");
                }
            }
        }
    }

    /// <summary>
    /// Applies a normal Harmony patch, but allows multiple targets
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    [MeansImplicitUse(ImplicitUseTargetFlags.WithMembers)]
    public abstract class MpCompatPatchAttribute : Attribute
    {
        private Type type;
        private string typeName;
        private string methodName;
        private Type[] argTypes;
        private MethodType methodType;
        private int? lambdaOrdinal;

        private MethodBase method;

        public Type Type
        {
            get
            {
                if (type != null)
                    return type;

                type = AccessTools.TypeByName(typeName);
                if (type == null)
                    throw new Exception($"Couldn't find type {typeName}");

                return type;
            }
        }

        public MethodBase Method
        {
            get
            {
                if (method != null)
                    return method;

                if (lambdaOrdinal != null)
                    return MpMethodUtil.GetLambda(Type, methodName, methodType, argTypes, lambdaOrdinal.Value);

                method = MpMethodUtil.GetMethod(Type, methodName, methodType, argTypes);
                if (method == null)
                    throw new MissingMethodException($"Couldn't find method {methodName} in type {Type}");

                return method;
            }
        }

        protected MpCompatPatchAttribute(Type type, string innerType, string methodName, MethodType methodType = MethodType.Normal) : this($"{type}+{innerType}", methodName)
        {
        }

        protected MpCompatPatchAttribute(string typeName, string methodName, MethodType methodType = MethodType.Normal)
        {
            this.typeName = typeName;
            this.methodName = methodName;
            this.methodType = methodType;
        }

        protected MpCompatPatchAttribute(Type type, string methodName, Type[] argTypes = null, MethodType methodType = MethodType.Normal)
        {
            this.type = type;
            this.methodName = methodName;
            this.argTypes = argTypes;
            this.methodType = methodType;
        }

        protected MpCompatPatchAttribute(Type type, MethodType methodType, Type[] argTypes = null)
        {
            this.type = type;
            this.methodType = methodType;
            this.argTypes = argTypes;
            this.methodType = methodType;
        }

        protected MpCompatPatchAttribute(Type type, string methodName, int lambdaOrdinal, MethodType methodType = MethodType.Normal)
        {
            this.type = type;
            this.methodName = methodName;
            this.lambdaOrdinal = lambdaOrdinal;
            this.methodType = methodType;
        }

        protected MpCompatPatchAttribute(string typeName, string methodName, int lambdaOrdinal, MethodType methodType = MethodType.Normal)
        {
            this.typeName = typeName;
            this.methodName = methodName;
            this.lambdaOrdinal = lambdaOrdinal;
            this.methodType = methodType;
        }
    }

    /// <summary>
    /// Prefix method attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class MpCompatPrefixAttribute : MpCompatPatchAttribute
    {
        public MpCompatPrefixAttribute(string typeName, string method, MethodType methodType = MethodType.Normal) : base(typeName, method, methodType)
        {
        }

        public MpCompatPrefixAttribute(Type type, string method, Type[] argTypes = null, MethodType methodType = MethodType.Normal) : base(type, method, argTypes, methodType)
        {
        }

        public MpCompatPrefixAttribute(Type type, string innerType, string method, MethodType methodType = MethodType.Normal) : base(type, innerType, method, methodType)
        {
        }

        public MpCompatPrefixAttribute(Type parentType, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(parentType, parentMethod, lambdaOrdinal, methodType)
        {
        }

        public MpCompatPrefixAttribute(string typeName, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(typeName, parentMethod, lambdaOrdinal, methodType)
        {
        }
    }

    /// <summary>
    /// Postfix method attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class MpCompatPostfixAttribute : MpCompatPatchAttribute
    {
        public MpCompatPostfixAttribute(string typeName, string method, MethodType methodType = MethodType.Normal) : base(typeName, method, methodType)
        {
        }

        public MpCompatPostfixAttribute(Type type, string method, Type[] argTypes = null, MethodType methodType = MethodType.Normal) : base(type, method, argTypes, methodType)
        {
        }

        public MpCompatPostfixAttribute(Type type, string innerType, string method, MethodType methodType = MethodType.Normal) : base(type, innerType, method, methodType)
        {
        }

        public MpCompatPostfixAttribute(Type parentType, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(parentType, parentMethod, lambdaOrdinal, methodType)
        {
        }

        public MpCompatPostfixAttribute(string typeName, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(typeName, parentMethod, lambdaOrdinal, methodType)
        {
        }
    }

    /// <summary>
    /// Finalizer method attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class MpCompatFinalizerAttribute : MpCompatPatchAttribute
    {
        public MpCompatFinalizerAttribute(string typeName, string method, MethodType methodType = MethodType.Normal) : base(typeName, method, methodType)
        {
        }

        public MpCompatFinalizerAttribute(Type type, string method, Type[] argTypes = null, MethodType methodType = MethodType.Normal) : base(type, method, argTypes, methodType)
        {
        }

        public MpCompatFinalizerAttribute(Type type, string innerType, string method, MethodType methodType = MethodType.Normal) : base(type, innerType, method, methodType)
        {
        }

        public MpCompatFinalizerAttribute(Type parentType, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(parentType, parentMethod, lambdaOrdinal, methodType)
        {
        }

        public MpCompatFinalizerAttribute(string typeName, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(typeName, parentMethod, lambdaOrdinal, methodType)
        {
        }
    }

    /// <summary>
    /// Transpiler method attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class MpCompatTranspilerAttribute : MpCompatPatchAttribute
    {
        public MpCompatTranspilerAttribute(string typeName, string method, MethodType methodType = MethodType.Normal) : base(typeName, method, methodType)
        {
        }

        public MpCompatTranspilerAttribute(Type type, string method, Type[] argTypes = null, MethodType methodType = MethodType.Normal) : base(type, method, argTypes, methodType)
        {
        }

        public MpCompatTranspilerAttribute(Type type, string innerType, string method, MethodType methodType = MethodType.Normal) : base(type, innerType, method, methodType)
        {
        }

        public MpCompatTranspilerAttribute(Type parentType, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(parentType, parentMethod, lambdaOrdinal, methodType)
        {
        }

        public MpCompatTranspilerAttribute(string typeName, string parentMethod, int lambdaOrdinal, MethodType methodType = MethodType.Normal) : base(typeName, parentMethod, lambdaOrdinal, methodType)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    [MeansImplicitUse(ImplicitUseTargetFlags.WithMembers)]
    public class MpCompatSyncMethodAttribute : Attribute
    {
        public SyncContext context;
        public bool cancelIfAnyArgNull;
        public bool cancelIfNoSelectedMapObjects;
        public bool cancelIfNoSelectedWorldObjects;
        public bool debugOnly;
        public int[] exposeParameters;

        public MpCompatSyncMethodAttribute(SyncContext context = SyncContext.None) => this.context = context;
    }

    [AttributeUsage(AttributeTargets.Field)]
    [MeansImplicitUse(ImplicitUseKindFlags.Assign)]
    public class MpCompatSyncFieldAttribute : Attribute
    {
        public SyncContext context;
        public bool cancelIfValueNull;
        public bool inGameLoop;
        public bool bufferChanges = true;
        public bool debugOnly;
        public bool hostOnly;
        public int version;

        private Type type;
        private string typeName;
        private string fieldName;

        private FieldInfo field;

        public Type Type
        {
            get
            {
                if (type != null)
                    return type;

                type = AccessTools.TypeByName(typeName);
                if (type == null)
                    throw new Exception($"Couldn't find type {typeName}");

                return type;
            }
        }

        public FieldInfo Field
        {
            get
            {
                if (field != null)
                    return field;

                field = AccessTools.DeclaredField(Type, fieldName);
                if (field == null)
                    throw new MissingMethodException($"Couldn't find field {field} in type {Type}");

                return field;
            }
        }

        protected MpCompatSyncFieldAttribute(SyncContext context = SyncContext.None) => this.context = context;

        public MpCompatSyncFieldAttribute(string typeName, string fieldName, SyncContext context = SyncContext.None) : this(context)
        {
            this.typeName = typeName;
            this.fieldName = fieldName;
        }

        public MpCompatSyncFieldAttribute(Type type, string fieldName, SyncContext context = SyncContext.None) : this(context)
        {
            this.type = type;
            this.fieldName = fieldName;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    [MeansImplicitUse(ImplicitUseTargetFlags.WithMembers)]
    public class MpCompatSyncWorkerAttribute : Attribute
    {
        public bool isImplicit;
        public bool shouldConstruct;

        private Type type;
        private string typeName;

        public Type Type
        {
            get
            {
                if (type != null)
                    return type;
                if (typeName == null)
                    return null;

                type = AccessTools.TypeByName(typeName);
                if (type == null)
                    throw new Exception($"Couldn't find type {typeName}");

                return type;
            }
        }

        public MpCompatSyncWorkerAttribute()
        {
        }

        public MpCompatSyncWorkerAttribute(Type type) => this.type = type;

        public MpCompatSyncWorkerAttribute(string typeName) => this.typeName = typeName;
    }
}