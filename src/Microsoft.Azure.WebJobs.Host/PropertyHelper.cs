﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;

namespace Microsoft.Azure.WebJobs.Host
{
    //internal interface IPropertyHelper
    //{
    //    /// <summary>
    //    /// Gets or sets the name of the property.
    //    /// </summary>
    //    string Name { get; }

    //    /// <summary>
    //    /// Gets the <see cref="Type"/> of the property.
    //    /// </summary>
    //    Type PropertyType { get; }

    //    /// <summary>
    //    /// Gets the value of the property for the specified instance.
    //    /// </summary>
    //    /// <param name="instance">The instance to return the property value for.</param>
    //    /// <returns>The property value.</returns>
    //    object GetValue(object instance);
    //}

    /// <summary>
    /// Class used to facilitate reflection operations.
    /// </summary>
    internal class PropertyHelper
    {
        private static readonly MethodInfo CallPropertyGetterOpenGenericMethod = typeof(PropertyHelper).GetMethod("CallPropertyGetter", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo CallPropertyGetterByReferenceOpenGenericMethod = typeof(PropertyHelper).GetMethod("CallPropertyGetterByReference", BindingFlags.NonPublic | BindingFlags.Static);
        // Implementation of the fast setter.
        private static readonly MethodInfo CallPropertySetterOpenGenericMethod = typeof(PropertyHelper).GetMethod("CallPropertySetter", BindingFlags.NonPublic | BindingFlags.Static);

        private static ConcurrentDictionary<Type, PropertyHelper[]> _reflectionCache = new ConcurrentDictionary<Type, PropertyHelper[]>();

        private readonly Type _propertyType;
        protected Func<object, object> _valueGetter;

        /// <summary>
        /// Initializes a fast property helper. This constructor does not cache the helper.
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors", Justification = "This is intended the Name is auto set differently per type and the type is internal")]
        public PropertyHelper(PropertyInfo property)
        {
            if (property == null)
            {
                throw new ArgumentNullException("property");
            }

            Name = property.Name;
            _propertyType = property.PropertyType;
            _valueGetter = MakeFastPropertyGetter(property);
        }

        protected PropertyHelper(string name, Type propertyType, Func<object, object> valueGetter)
        {
            Name = name;
            _propertyType = propertyType;
            _valueGetter = valueGetter;
        }

        // Implementation of the fast getter.
        private delegate TValue ByRefFunc<TDeclaringType, TValue>(ref TDeclaringType arg);

        /// <summary>
        /// Gets or sets the name of the property.
        /// </summary>
        public virtual string Name { get; private set; }

        /// <summary>
        /// Gets the <see cref="Type"/> of the property.
        /// </summary>
        public Type PropertyType
        {
            get { return _propertyType; }
        }

        /// <summary>
        /// Gets the value of the property for the specified instance.
        /// </summary>
        /// <param name="instance">The instance to return the property value for.</param>
        /// <returns>The property value.</returns>
        public object GetValue(object instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException("instance");
            }

            return _valueGetter(instance);
        }

        /// <summary>
        /// Creates and caches fast property helpers that expose getters for every public get property on the underlying type.
        /// </summary>
        /// <param name="instance">the instance to extract property accessors for.</param>
        /// <returns>a cached array of all public property getters from the underlying type of this instance.</returns>
        public static PropertyHelper[] GetProperties(object instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException("instance");
            }

            return GetProperties(instance.GetType());
        }

        /// <summary>
        /// Returns a collection of <see cref="PropertyHelper"/>s for the specified <see cref="Type"/>.
        /// </summary>
        /// <param name="type">The type to return <see cref="PropertyHelper"/>s for.</param>
        /// <param name="getExtendedPropertyHelpers"></param>
        /// <returns>A collection of <see cref="PropertyHelper"/>s.</returns>
        public static PropertyHelper[] GetProperties(Type type, int getExtendedPropertyHelpers = 2)
        {
            return GetProperties(type, CreateInstance, _reflectionCache, getExtendedPropertyHelpers);
        }

        /// <summary>
        /// Creates a single fast property setter. The result is not cached.
        /// </summary>
        /// <param name="property">The property to extract the getter for.</param>
        /// <returns>a fast setter.</returns>
        /// <remarks>This method is more memory efficient than a dynamically compiled lambda, and about the same speed.</remarks>
        private static Action<TDeclaringType, object> MakeFastPropertySetter<TDeclaringType>(PropertyInfo property)
            where TDeclaringType : class
        {
            if (property == null)
            {
                throw new ArgumentNullException("property");
            }

            MethodInfo setMethod = property.GetSetMethod();

            Contract.Assert(setMethod != null);
            Contract.Assert(!setMethod.IsStatic);
            Contract.Assert(setMethod.GetParameters().Length == 1);
            Contract.Assert(!property.ReflectedType.IsValueType);

            // Instance methods in the CLR can be turned into static methods where the first parameter
            // is open over "this". This parameter is always passed by reference, so we have a code
            // path for value types and a code path for reference types.
            Type typeInput = property.ReflectedType;
            Type typeValue = setMethod.GetParameters()[0].ParameterType;

            Delegate callPropertySetterDelegate;

            // Create a delegate TValue -> "TDeclaringType.Property"
            var propertySetterAsAction = setMethod.CreateDelegate(typeof(Action<,>).MakeGenericType(typeInput, typeValue));
            var callPropertySetterClosedGenericMethod = CallPropertySetterOpenGenericMethod.MakeGenericMethod(typeInput, typeValue);
            callPropertySetterDelegate = Delegate.CreateDelegate(typeof(Action<TDeclaringType, object>), propertySetterAsAction, callPropertySetterClosedGenericMethod);

            return (Action<TDeclaringType, object>)callPropertySetterDelegate;
        }

        /// <summary>
        /// Creates a single fast property getter. The result is not cached.
        /// </summary>
        /// <param name="propertyInfo">propertyInfo to extract the getter for.</param>
        /// <returns>a fast getter.</returns>
        /// <remarks>This method is more memory efficient than a dynamically compiled lambda, and about the same speed.</remarks>
        private static Func<object, object> MakeFastPropertyGetter(PropertyInfo propertyInfo)
        {
            Contract.Assert(propertyInfo != null);

            MethodInfo getMethod = propertyInfo.GetGetMethod();
            Contract.Assert(getMethod != null);
            Contract.Assert(!getMethod.IsStatic);
            Contract.Assert(getMethod.GetParameters().Length == 0);

            // Instance methods in the CLR can be turned into static methods where the first parameter
            // is open over "this". This parameter is always passed by reference, so we have a code
            // path for value types and a code path for reference types.
            Type typeInput = getMethod.ReflectedType;
            Type typeOutput = getMethod.ReturnType;

            Delegate callPropertyGetterDelegate;
            if (typeInput.IsValueType)
            {
                // Create a delegate (ref TDeclaringType) -> TValue
                Delegate propertyGetterAsFunc = getMethod.CreateDelegate(typeof(ByRefFunc<,>).MakeGenericType(typeInput, typeOutput));
                MethodInfo callPropertyGetterClosedGenericMethod = CallPropertyGetterByReferenceOpenGenericMethod.MakeGenericMethod(typeInput, typeOutput);
                callPropertyGetterDelegate = Delegate.CreateDelegate(typeof(Func<object, object>), propertyGetterAsFunc, callPropertyGetterClosedGenericMethod);
            }
            else
            {
                // Create a delegate TDeclaringType -> TValue
                Delegate propertyGetterAsFunc = getMethod.CreateDelegate(typeof(Func<,>).MakeGenericType(typeInput, typeOutput));
                MethodInfo callPropertyGetterClosedGenericMethod = CallPropertyGetterOpenGenericMethod.MakeGenericMethod(typeInput, typeOutput);
                callPropertyGetterDelegate = Delegate.CreateDelegate(typeof(Func<object, object>), propertyGetterAsFunc, callPropertyGetterClosedGenericMethod);
            }

            return (Func<object, object>)callPropertyGetterDelegate;
        }

        private static PropertyHelper CreateInstance(PropertyInfo property)
        {
            return new PropertyHelper(property);
        }

        private static object CallPropertyGetter<TDeclaringType, TValue>(Func<TDeclaringType, TValue> getter, object @this)
        {
            return getter((TDeclaringType)@this);
        }

        private static object CallPropertyGetterByReference<TDeclaringType, TValue>(ByRefFunc<TDeclaringType, TValue> getter, object @this)
        {
            TDeclaringType unboxed = (TDeclaringType)@this;
            return getter(ref unboxed);
        }

        private static void CallPropertySetter<TDeclaringType, TValue>(Action<TDeclaringType, TValue> setter, object @this, object value)
        {
            setter((TDeclaringType)@this, (TValue)value);
        }

        private static PropertyHelper[] GetProperties(Type type,
                                                        Func<PropertyInfo, PropertyHelper> createPropertyHelper,
                                                        ConcurrentDictionary<Type, PropertyHelper[]> cache,
                                                        int getExtendedPropertyHelpers)
        {
            // Using an array rather than IEnumerable, as this will be called on the hot path numerous times.
            PropertyHelper[] helpers;

            if (!cache.TryGetValue(type, out helpers))
            {
                // We avoid loading indexed properties using the where statement.
                // Indexed properties are not useful (or valid) for grabbing properties off an anonymous object.
                IEnumerable<PropertyInfo> properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                           .Where(prop => prop.GetIndexParameters().Length == 0 &&
                                                                          prop.GetMethod != null);

                var newHelpers = new List<PropertyHelper>();

                foreach (PropertyInfo property in properties)
                {
                    PropertyHelper propertyHelper = createPropertyHelper(property);

                    newHelpers.Add(propertyHelper);

                    if (getExtendedPropertyHelpers > 0)
                    {
                        var extendedPropertyHelpers = GetProperties(propertyHelper.PropertyType, getExtendedPropertyHelpers - 1);

                        newHelpers.AddRange(
                            from extendedPropertyHelper in extendedPropertyHelpers
                            let extendedPropertyName = propertyHelper.Name + "." + extendedPropertyHelper.Name
                            let extendedPropertyValueGetter =
                                (Func<object, object>)
                                    (o =>
                                    {
                                        var containerValue = propertyHelper.GetValue(o);
                                        if (containerValue == null)
                                            return null;
                                        return extendedPropertyHelper.GetValue(containerValue);
                                    })
                            select
                                new ExtendedPropertyHelper(
                                    extendedPropertyName,
                                    extendedPropertyHelper.PropertyType,
                                    extendedPropertyValueGetter)
                            );
                    }
                }

                helpers = newHelpers.ToArray();
                cache.TryAdd(type, helpers);
            }

            return helpers;
        }
    }

    internal class ExtendedPropertyHelper : PropertyHelper
    {
        public ExtendedPropertyHelper(string name, Type type, Func<object, object> valueGetter) : base(name, type, valueGetter)
        {
        }
    }
}
