using System;
using System.Collections.Generic;
using System.Reflection;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Holds a unique classId plus a constructor function for an entity type.
    /// </summary>
    public struct RegisteredTypeInfo
    {
        public readonly ushort ClassId;
        public readonly EntityConstructor<InternalEntity> Constructor;

        public RegisteredTypeInfo(
            ushort classId,
            EntityConstructor<InternalEntity> constructor
        )
        {
            ClassId = classId;
            Constructor = constructor;
        }
    }

    /// <summary>
    /// Base abstract class that contains:
    /// - A dictionary of (Type -> RegisteredTypeInfo)
    /// - Reflection-based hashing logic to detect mismatch
    /// </summary>
    public abstract class EntityTypesMap
    {
        /// <summary>
        /// The highest classId encountered during registrations.
        /// </summary>
        internal ushort MaxId;

        /// <summary>
        /// Main dictionary from C# Type -> (ClassId + constructor)
        /// </summary>
        internal readonly Dictionary<Type, RegisteredTypeInfo> RegisteredTypes
            = new Dictionary<Type, RegisteredTypeInfo>();

        private bool _finished;
        private ulong _resultHash = 14695981039346656037UL; // FNV-1a offset

        // optional reflection caching
        private static readonly Dictionary<Type, FieldInfo[]> s_fieldsCache
            = new Dictionary<Type, FieldInfo[]>();

        private static FieldInfo[] GetProcessedFieldsCached(Type t)
        {
            if (!s_fieldsCache.TryGetValue(t, out var fields))
            {
                // your utility method
                fields = Utils.GetProcessedFields(t);
                s_fieldsCache[t] = fields;
            }

            return fields;
        }

        /// <summary>
        /// Returns a 64-bit hash to detect mismatches between client/server 
        /// entity definitions (FNV-1a).
        /// 
        /// - Skips AiControllerLogic-based classes.
        /// - Recursively processes SyncableField subfields.
        /// 
        /// If you only store references to the base class (EntityTypesMap) 
        /// instead of the generic one, you'll still have this hashing logic.
        /// </summary>
        public ulong EvaluateEntityClassDataHash()
        {
            if (_finished)
                return _resultHash;

            // gather each type
            var list = new List<(Type, RegisteredTypeInfo)>(RegisteredTypes.Count);
            foreach (var kvp in RegisteredTypes)
            {
                Type entityType = kvp.Key;
                var info = kvp.Value;

                // skip classes that derive from AiControllerLogic 
                // if they're "local only"
                if (!entityType.IsSubclassOf(typeof(AiControllerLogic)))
                {
                    list.Add((entityType, info));
                }
            }

            // sort by classId
            list.Sort((a, b) => a.Item2.ClassId.CompareTo(b.Item2.ClassId));

            // reflection pass
            foreach (var (entType, _) in list)
            {
                // your signature: (Type start, Type limit, bool includeSelf, bool reverse)
                var stack = Utils.GetBaseTypes(entType, typeof(InternalEntity), true, true);

                while (stack.Count > 0)
                {
                    var baseType = stack.Pop();
                    var fields = GetProcessedFieldsCached(baseType);

                    foreach (var fi in fields)
                    {
                        // if it's a SyncableField, also process subfields
                        if (fi.FieldType.IsSubclassOf(typeof(SyncableField)))
                        {
                            var subfields = GetProcessedFieldsCached(fi.FieldType);
                            foreach (var subFi in subfields)
                            {
                                HashOneField(subFi);
                            }
                        }
                        else
                        {
                            HashOneField(fi);
                        }
                    }
                }
            }

            _finished = true;
            return _resultHash;

            // local function to do the FNV-1a hashing
            void HashOneField(FieldInfo fi)
            {
                var ft = fi.FieldType;

                bool isStaticRemote = fi.IsStatic && Utils.IsRemoteCallType(ft);
                bool isGenericSyncVar = ft.IsGenericType
                                        && !ft.IsArray
                                        && ft.GetGenericTypeDefinition() == typeof(SyncVar<>);

                if (isStaticRemote || isGenericSyncVar)
                {
                    // combine type name, plus generics
                    string typeSig = ft.Name;
                    if (ft.IsGenericType)
                    {
                        var genArg = ft.GetGenericArguments()[0];
                        typeSig += "<" + genArg.Name + ">";
                    }

                    for (int i = 0; i < typeSig.Length; i++)
                    {
                        _resultHash ^= typeSig[i];
                        _resultHash *= 1099511628211UL;
                    }
                }
            }
        }
    }

    /// <summary>
    /// A typed map that associates your int-based enum T with entity constructors.
    /// 
    /// Usage:
    ///   var map = new EntityTypesMap<MyEnum>()
    ///       .Register(MyEnum.Player, p => new PlayerEntity(p))
    ///       .Register(MyEnum.Monster, p => new MonsterEntity(p));
    /// 
    /// Then store it as 'EntityTypesMap<MyEnum>' to call TryGetRegisteredInfo.
    /// </summary>
    public sealed class EntityTypesMap<T> : EntityTypesMap
        where T : unmanaged, Enum
    {
        private readonly Dictionary<T, RegisteredTypeInfo> _byEnum
            = new Dictionary<T, RegisteredTypeInfo>();

        /// <summary>
        /// Register a new entity type with your enum key.
        /// The ClassId is auto-derived from (int)enumValue + 1.
        /// Example: If T is MyEnum.Spider=4, ClassId=5.
        /// </summary>
        public EntityTypesMap<T> Register<TEntity>(
            T enumId,
            EntityConstructor<TEntity> constructor
        )
            where TEntity : InternalEntity
        {
            int rawValue = Convert.ToInt32(enumId);
            ushort classId = (ushort)(rawValue + 1);

            // link the classId to TEntity statically
            EntityClassInfo<TEntity>.ClassId = classId;

            // build the reg info
            var regInfo = new RegisteredTypeInfo(
                classId,
                (EntityParams p) => constructor(p)
            );

            // store in the base dictionary
            RegisteredTypes.Add(typeof(TEntity), regInfo);

            // store in our typed dictionary
            _byEnum.Add(enumId, regInfo);

            // track the largest classId
            if (classId > MaxId)
                MaxId = classId;

            return this;
        }

        /// <summary>
        /// Attempt to retrieve the RegisteredTypeInfo by your enum key.
        /// Returns false if not found.
        /// 
        /// NOTE: If you store your map as just 'EntityTypesMap', 
        /// you can't see this method. Keep it typed as 'EntityTypesMap<T>'.
        /// </summary>
        public bool TryGetRegisteredInfo(T enumId, out RegisteredTypeInfo info)
        {
            return _byEnum.TryGetValue(enumId, out info);
        }
    }
}