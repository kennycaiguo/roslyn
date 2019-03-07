﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Roslyn.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal enum CSharpNullableAnnotation : byte
    {
        Unknown,      // No information. Think oblivious.
        NotAnnotated, // Type is not annotated - string, int, T (including the case when T is unconstrained).
        Annotated,    // Type is annotated - string?, T? where T : class; and for int?, T? where T : struct.
        NotNullable,  // Explicitly set by flow analysis
        Nullable,     // Explicitly set by flow analysis
    }

    /// <summary>
    /// A type and its corresponding flow state resulting from evaluating an rvalue expression.
    /// </summary>
    [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
    internal readonly struct TypeWithState
    {
        public TypeSymbol Type { get; }
        public NullableFlowState State { get; }
        public bool HasNullType => Type is null;
        public bool MaybeNull => State == NullableFlowState.MaybeNull;
        public bool NotNull => State == NullableFlowState.NotNull;
        public static TypeWithState ForType(TypeSymbol type) => new TypeWithState(type, type?.CanContainNull() == true ? NullableFlowState.MaybeNull : NullableFlowState.NotNull);
        public TypeWithState(TypeSymbol type, NullableFlowState state) => (Type, State) = (type, state);
        public void Deconstruct(out TypeSymbol type, out NullableFlowState state) => (type, state) = (Type, State);
        public string GetDebuggerDisplay() => $"{{Type:{Type?.GetDebuggerDisplay()}, State:{State}{"}"}";
        public TypeWithState WithNotNullState() => new TypeWithState(Type, NullableFlowState.NotNull);
        public TypeSymbolWithAnnotations ToTypeSymbolWithAnnotations()
        {
            CSharpNullableAnnotation annotation = (this.State == NullableFlowState.NotNull)
                ? CSharpNullableAnnotation.NotNullable : CSharpNullableAnnotation.Nullable;
            return TypeSymbolWithAnnotations.Create(this.Type, annotation);
        }
    }

    internal static class CSharpNullableAnnotationExtensions
    {
        public static bool IsAnyNullable(this CSharpNullableAnnotation annotation)
        {
            return annotation == CSharpNullableAnnotation.Annotated || annotation == CSharpNullableAnnotation.Nullable;
        }

        public static bool IsAnyNotNullable(this CSharpNullableAnnotation annotation)
        {
            return annotation == CSharpNullableAnnotation.NotAnnotated || annotation == CSharpNullableAnnotation.NotNullable;
        }

        public static bool IsSpeakable(this CSharpNullableAnnotation annotation)
        {
            return annotation == CSharpNullableAnnotation.Unknown ||
                annotation == CSharpNullableAnnotation.NotAnnotated ||
                annotation == CSharpNullableAnnotation.Annotated;
        }

        /// <summary>
        /// This method projects nullable annotations onto a smaller set that can be expressed in source.
        /// </summary>
        public static CSharpNullableAnnotation AsSpeakable(this CSharpNullableAnnotation annotation, TypeSymbol type)
        {
            if (type is null && annotation == CSharpNullableAnnotation.Unknown)
            {
                return default;
            }

            Debug.Assert((object)type != null);
            switch (annotation)
            {
                case CSharpNullableAnnotation.Unknown:
                case CSharpNullableAnnotation.NotAnnotated:
                case CSharpNullableAnnotation.Annotated:
                    return annotation;

                case CSharpNullableAnnotation.Nullable:
                    if (type.IsTypeParameterDisallowingAnnotation())
                    {
                        return CSharpNullableAnnotation.NotAnnotated;
                    }
                    return CSharpNullableAnnotation.Annotated;

                case CSharpNullableAnnotation.NotNullable:
                    // Example of unspeakable types:
                    // - an unconstrained T which was null-tested already
                    // - a nullable value type which was null-tested already
                    // Note this projection is lossy for such types (we forget about the non-nullable state)
                    return CSharpNullableAnnotation.NotAnnotated;

                default:
                    throw ExceptionUtilities.UnexpectedValue(annotation);
            }
        }

        /// <summary>
        /// Join nullable annotations from the set of lower bounds for fixing a type parameter.
        /// This uses the covariant merging rules.
        /// </summary>
        public static CSharpNullableAnnotation JoinForFixingLowerBounds(this CSharpNullableAnnotation a, CSharpNullableAnnotation b)
        {
            if (a == CSharpNullableAnnotation.Nullable || b == CSharpNullableAnnotation.Nullable)
            {
                return CSharpNullableAnnotation.Nullable;
            }

            if (a == CSharpNullableAnnotation.Annotated || b == CSharpNullableAnnotation.Annotated)
            {
                return CSharpNullableAnnotation.Annotated;
            }

            if (a == CSharpNullableAnnotation.Unknown || b == CSharpNullableAnnotation.Unknown)
            {
                return CSharpNullableAnnotation.Unknown;
            }

            if (a == CSharpNullableAnnotation.NotNullable || b == CSharpNullableAnnotation.NotNullable)
            {
                return CSharpNullableAnnotation.NotNullable;
            }

            return CSharpNullableAnnotation.NotAnnotated;
        }

        /// <summary>
        /// Join nullable flow states from distinct branches during flow analysis.
        /// </summary>
        public static NullableFlowState JoinForFlowAnalysisBranches(this NullableFlowState selfState, NullableFlowState otherState)
        {
            return (selfState == NullableFlowState.MaybeNull || otherState == NullableFlowState.MaybeNull)
                ? NullableFlowState.MaybeNull : NullableFlowState.NotNull;
        }

        /// <summary>
        /// Meet two nullable annotations for computing the nullable annotation of a type parameter from upper bounds.
        /// This uses the contravariant merging rules.
        /// </summary>
        public static CSharpNullableAnnotation MeetForFixingUpperBounds(this CSharpNullableAnnotation a, CSharpNullableAnnotation b)
        {
            if (a == CSharpNullableAnnotation.NotNullable || b == CSharpNullableAnnotation.NotNullable)
            {
                return CSharpNullableAnnotation.NotNullable;
            }

            if (a == CSharpNullableAnnotation.NotAnnotated || b == CSharpNullableAnnotation.NotAnnotated)
            {
                return CSharpNullableAnnotation.NotAnnotated;
            }

            if (a == CSharpNullableAnnotation.Unknown || b == CSharpNullableAnnotation.Unknown)
            {
                return CSharpNullableAnnotation.Unknown;
            }

            if (a == CSharpNullableAnnotation.Nullable || b == CSharpNullableAnnotation.Nullable)
            {
                return CSharpNullableAnnotation.Nullable;
            }

            return CSharpNullableAnnotation.Annotated;
        }

        /// <summary>
        /// Meet two nullable flow states from distinct states for the meet (union) operation in flow analysis.
        /// </summary>
        public static NullableFlowState MeetForFlowAnalysisFinally(this NullableFlowState selfState, NullableFlowState otherState)
        {
            return (selfState == NullableFlowState.NotNull || otherState == NullableFlowState.NotNull)
                ? NullableFlowState.NotNull : NullableFlowState.MaybeNull;
        }

        /// <summary>
        /// Check that two nullable annotations are "compatible", which means they could be the same. Return the
        /// nullable annotation to be used as a result.
        /// This uses the invariant merging rules.
        /// </summary>
        public static CSharpNullableAnnotation EnsureCompatible(this CSharpNullableAnnotation a, CSharpNullableAnnotation b)
        {
            Debug.Assert(a.IsSpeakable());
            Debug.Assert(b.IsSpeakable());

            if (a == CSharpNullableAnnotation.NotAnnotated || b == CSharpNullableAnnotation.NotAnnotated)
            {
                return CSharpNullableAnnotation.NotAnnotated;
            }

            if (a == CSharpNullableAnnotation.Annotated || b == CSharpNullableAnnotation.Annotated)
            {
                return CSharpNullableAnnotation.Annotated;
            }

            return CSharpNullableAnnotation.Unknown;
        }

        /// <summary>
        /// Check that two nullable annotations are "compatible", which means they could be the same. Return the
        /// nullable annotation to be used as a result. This method can handle unspeakable types (for merging tuple types).
        /// </summary>
        public static CSharpNullableAnnotation EnsureCompatibleForTuples(this CSharpNullableAnnotation a, CSharpNullableAnnotation b)
        {
            if (a == CSharpNullableAnnotation.NotNullable || b == CSharpNullableAnnotation.NotNullable)
            {
                return CSharpNullableAnnotation.NotNullable;
            }

            if (a == CSharpNullableAnnotation.NotAnnotated || b == CSharpNullableAnnotation.NotAnnotated)
            {
                return CSharpNullableAnnotation.NotAnnotated;
            }

            if (a == CSharpNullableAnnotation.Nullable || b == CSharpNullableAnnotation.Nullable)
            {
                return CSharpNullableAnnotation.Nullable;
            }

            if (a == CSharpNullableAnnotation.Annotated || b == CSharpNullableAnnotation.Annotated)
            {
                return CSharpNullableAnnotation.Annotated;
            }

            return CSharpNullableAnnotation.Unknown;
        }

        internal static NullableAnnotation ToPublicAnnotation(this CSharpNullableAnnotation annotation)
        {
            Debug.Assert((NullableAnnotation)(CSharpNullableAnnotation.Unknown + 1) == NullableAnnotation.Unknown);
            return (NullableAnnotation)annotation + 1;
        }
    }

    internal static class NullableAnnotationExtensions
    {
        public static CSharpNullableAnnotation ToCSharpAnnotation(this NullableAnnotation annotation)
        {
            Debug.Assert((NullableAnnotation)(CSharpNullableAnnotation.Unknown + 1) == NullableAnnotation.Unknown);
            return annotation == NullableAnnotation.Default ? CSharpNullableAnnotation.Unknown : (CSharpNullableAnnotation)annotation - 1;
        }
    }

    /// <summary>
    /// A simple class that combines a single type symbol with annotations
    /// </summary>
    [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
    internal readonly struct TypeSymbolWithAnnotations : IFormattable
    {
        /// <summary>
        /// A builder for lazy instances of TypeSymbolWithAnnotations.
        /// </summary>
        [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
        internal struct Builder
        {
            private TypeSymbol _defaultType;
            private CSharpNullableAnnotation _nullableAnnotation;
            private Extensions _extensions;

            /// <summary>
            /// The underlying type, unless overridden by _extensions.
            /// </summary>
            internal TypeSymbol DefaultType => _defaultType;

            /// <summary>
            /// True if the fields of the builder are unset.
            /// </summary>
            internal bool IsDefault => _defaultType is null && _nullableAnnotation == 0 && (_extensions == null || _extensions == Extensions.Default);

            /// <summary>
            /// Set the fields of the builder.
            /// </summary>
            /// <remarks>
            /// This method guarantees: fields will be set once; exactly one caller is
            /// returned true; and IsNull will return true until all fields are initialized.
            /// This method does not guarantee that all fields will be set by the same
            /// caller. Instead, the expectation is that all callers will attempt to initialize
            /// the builder with equivalent TypeSymbolWithAnnotations instances where
            /// different fields of the builder may be assigned from different instances.
            /// </remarks>
            internal bool InterlockedInitialize(TypeSymbolWithAnnotations type)
            {
                if ((object)_defaultType != null)
                {
                    return false;
                }
                _nullableAnnotation = type.NullableAnnotation;
                Interlocked.CompareExchange(ref _extensions, type._extensions, null);
                return (object)Interlocked.CompareExchange(ref _defaultType, type._defaultType, null) == null;
            }

            /// <summary>
            /// Create immutable TypeSymbolWithAnnotations instance.
            /// </summary>
            internal TypeSymbolWithAnnotations ToType()
            {
                return IsDefault ?
                    default :
                    new TypeSymbolWithAnnotations(_defaultType, _nullableAnnotation, _extensions);
            }

            internal string GetDebuggerDisplay() => ToType().GetDebuggerDisplay();
        }

        /// <summary>
        /// The underlying type, unless overridden by _extensions.
        /// </summary>
        private readonly TypeSymbol _defaultType;

        /// <summary>
        /// Additional data or behavior. Such cases should be
        /// uncommon to minimize allocations.
        /// </summary>
        private readonly Extensions _extensions;

        public readonly CSharpNullableAnnotation NullableAnnotation;

        private TypeSymbolWithAnnotations(TypeSymbol defaultType, CSharpNullableAnnotation nullableAnnotation, Extensions extensions)
        {
            Debug.Assert(defaultType?.IsNullableType() != true || (nullableAnnotation != CSharpNullableAnnotation.Unknown && nullableAnnotation != CSharpNullableAnnotation.NotAnnotated));
            Debug.Assert(extensions != null);

            _defaultType = defaultType;
            NullableAnnotation = nullableAnnotation;
            _extensions = extensions;
        }

        public TypeSymbolWithAnnotations AsSpeakable()
        {
            if (!HasType)
            {
                return default;
            }

            TypeSymbol typeSymbol = this.TypeSymbol;
            var annotation = this.NullableAnnotation;
            var speakableAnnotation = annotation.AsSpeakable(typeSymbol);

            if (annotation == speakableAnnotation)
            {
                return this;
            }

            return Create(typeSymbol, speakableAnnotation, this.CustomModifiers);
        }

        public override string ToString() => TypeSymbol.ToString();
        public string Name => TypeSymbol.Name;
        public SymbolKind Kind => TypeSymbol.Kind;

        internal static readonly SymbolDisplayFormat DebuggerDisplayFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        internal static readonly SymbolDisplayFormat TestDisplayFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier,
            compilerInternalOptions: SymbolDisplayCompilerInternalOptions.IncludeNonNullableTypeModifier);

        internal static TypeSymbolWithAnnotations Create(bool isNullableEnabled, TypeSymbol typeSymbol, bool isAnnotated = false, ImmutableArray<CustomModifier> customModifiers = default)
        {
            if (typeSymbol is null)
            {
                return default;
            }

            return Create(typeSymbol, CSharpNullableAnnotation: isAnnotated ? CSharpNullableAnnotation.Annotated : isNullableEnabled ? CSharpNullableAnnotation.NotAnnotated : CSharpNullableAnnotation.Unknown,
                          customModifiers.NullToEmpty());
        }

        internal static TypeSymbolWithAnnotations Create(TypeSymbol typeSymbol, CSharpNullableAnnotation CSharpNullableAnnotation = CSharpNullableAnnotation.Unknown, ImmutableArray<CustomModifier> customModifiers = default)
        {
            if (typeSymbol is null && CSharpNullableAnnotation == 0)
            {
                return default;
            }

            switch (CSharpNullableAnnotation)
            {
                case CSharpNullableAnnotation.Unknown:
                case CSharpNullableAnnotation.NotAnnotated:
                    if (typeSymbol?.IsNullableType() == true)
                    {
                        // int?, T? where T : struct (add annotation)
                        CSharpNullableAnnotation = CSharpNullableAnnotation.Annotated;
                    }
                    break;
            }

            return CreateNonLazyType(typeSymbol, CSharpNullableAnnotation, customModifiers.NullToEmpty());
        }

        internal bool IsPossiblyNullableTypeTypeParameter()
        {
            return NullableAnnotation == CSharpNullableAnnotation.NotAnnotated &&
                (TypeSymbol?.IsPossiblyNullableReferenceTypeTypeParameter() == true ||
                 TypeSymbol?.IsNullableTypeOrTypeParameter() == true);
        }

        internal CSharpNullableAnnotation GetValueNullableAnnotation()
        {
            if (IsPossiblyNullableTypeTypeParameter())
            {
                return CSharpNullableAnnotation.Nullable;
            }

            // https://github.com/dotnet/roslyn/issues/31675: Is a similar case needed in ValueCanBeNull?
            if (NullableAnnotation != CSharpNullableAnnotation.NotNullable && IsNullableTypeOrTypeParameter())
            {
                return CSharpNullableAnnotation.Nullable;
            }

            return NullableAnnotation;
        }

        internal bool CanBeAssignedNull
        {
            get
            {
                switch (NullableAnnotation)
                {
                    case CSharpNullableAnnotation.Unknown:
                        return true;

                    case CSharpNullableAnnotation.Annotated:
                    case CSharpNullableAnnotation.Nullable:
                        return true;

                    case CSharpNullableAnnotation.NotNullable:
                        return false;

                    case CSharpNullableAnnotation.NotAnnotated:
                        return TypeSymbol.IsNullableType();

                    default:
                        throw ExceptionUtilities.UnexpectedValue(NullableAnnotation);
                }
            }
        }

        private static bool IsIndexedTypeParameter(TypeSymbol typeSymbol)
        {
            return typeSymbol is IndexedTypeParameterSymbol ||
                   typeSymbol is IndexedTypeParameterSymbolForOverriding;
        }

        private static TypeSymbolWithAnnotations CreateNonLazyType(TypeSymbol typeSymbol, CSharpNullableAnnotation CSharpNullableAnnotation, ImmutableArray<CustomModifier> customModifiers)
        {
            return new TypeSymbolWithAnnotations(typeSymbol, CSharpNullableAnnotation, Extensions.Create(customModifiers));
        }

        private static TypeSymbolWithAnnotations CreateLazyNullableType(CSharpCompilation compilation, TypeSymbolWithAnnotations underlying)
        {
            return new TypeSymbolWithAnnotations(defaultType: underlying._defaultType, nullableAnnotation: CSharpNullableAnnotation.Annotated, Extensions.CreateLazy(compilation, underlying));
        }

        /// <summary>
        /// True if the fields are unset. Appropriate when detecting if a lazily-initialized variable has been initialized.
        /// </summary>
        internal bool IsDefault => _defaultType is null && this.NullableAnnotation == 0 && (_extensions == null || _extensions == Extensions.Default);

        /// <summary>
        /// True if the type is not null.
        /// </summary>
        internal bool HasType => !(_defaultType is null);

        public TypeSymbolWithAnnotations SetIsAnnotated(CSharpCompilation compilation)
        {
            Debug.Assert(CustomModifiers.IsEmpty);

            var typeSymbol = this.TypeSymbol;

            // It is not safe to check if a type parameter is a reference type right away, this can send us into a cycle.
            // In this case we delay asking this question as long as possible.
            if (typeSymbol.TypeKind != TypeKind.TypeParameter)
            {
                if (!typeSymbol.IsValueType && !typeSymbol.IsErrorType())
                {
                    return CreateNonLazyType(typeSymbol, CSharpNullableAnnotation.Annotated, this.CustomModifiers);
                }
                else
                {
                    return Create(compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(ImmutableArray.Create(typeSymbol)));
                }
            }

            return CreateLazyNullableType(compilation, this);
        }

        private TypeSymbolWithAnnotations AsNullableReferenceType() => _extensions.AsNullableReferenceType(this);
        public TypeSymbolWithAnnotations AsNotNullableReferenceType() => _extensions.AsNotNullableReferenceType(this);

        /// <summary>
        /// Merges top-level and nested nullability from an otherwise identical type.
        /// </summary>
        internal TypeSymbolWithAnnotations MergeNullability(TypeSymbolWithAnnotations other, VarianceKind variance)
        {
            Debug.Assert(this.NullableAnnotation.IsSpeakable());
            Debug.Assert(other.NullableAnnotation.IsSpeakable());

            TypeSymbol typeSymbol = other.TypeSymbol;
            CSharpNullableAnnotation CSharpNullableAnnotation = MergeNullableAnnotation(this.NullableAnnotation, other.NullableAnnotation, variance);
            TypeSymbol type = TypeSymbol.MergeNullability(typeSymbol, variance);
            Debug.Assert((object)type != null);
            return Create(type, CSharpNullableAnnotation, CustomModifiers);
        }

        /// <summary>
        /// Merges nullability.
        /// </summary>
        private static CSharpNullableAnnotation MergeNullableAnnotation(CSharpNullableAnnotation a, CSharpNullableAnnotation b, VarianceKind variance)
        {
            Debug.Assert(a.IsSpeakable());
            Debug.Assert(b.IsSpeakable());

            switch (variance)
            {
                case VarianceKind.In:
                    return a.MeetForFixingUpperBounds(b);
                case VarianceKind.Out:
                    return a.JoinForFixingLowerBounds(b);
                case VarianceKind.None:
                    return a.EnsureCompatible(b);
                default:
                    throw ExceptionUtilities.UnexpectedValue(variance);
            }
        }

        public TypeSymbolWithAnnotations WithModifiers(ImmutableArray<CustomModifier> customModifiers) =>
            _extensions.WithModifiers(this, customModifiers);

        public TypeSymbol TypeSymbol => _extensions?.GetResolvedType(_defaultType);
        public TypeSymbol NullableUnderlyingTypeOrSelf => _extensions.GetNullableUnderlyingTypeOrSelf(_defaultType);

        /// <summary>
        /// Is this System.Nullable`1 type, or its substitution.
        /// </summary>
        public bool IsNullableType() => TypeSymbol.IsNullableType();

        /// <summary>
        /// The list of custom modifiers, if any, associated with the <see cref="TypeSymbol"/>.
        /// </summary>
        public ImmutableArray<CustomModifier> CustomModifiers => _extensions.CustomModifiers;

        public bool IsReferenceType => TypeSymbol.IsReferenceType;
        public bool IsValueType => TypeSymbol.IsValueType;
        public TypeKind TypeKind => TypeSymbol.TypeKind;
        public SpecialType SpecialType => _extensions.GetSpecialType(_defaultType);
        public bool IsManagedType => TypeSymbol.IsManagedType;
        public Cci.PrimitiveTypeCode PrimitiveTypeCode => TypeSymbol.PrimitiveTypeCode;
        public bool IsEnumType() => TypeSymbol.IsEnumType();
        public bool IsDynamic() => TypeSymbol.IsDynamic();
        public bool IsObjectType() => TypeSymbol.IsObjectType();
        public bool IsArray() => TypeSymbol.IsArray();
        public bool IsRestrictedType(bool ignoreSpanLikeTypes = false) =>
            _extensions.IsRestrictedType(_defaultType, ignoreSpanLikeTypes);
        public bool IsPointerType() => TypeSymbol.IsPointerType();
        public bool IsErrorType() => TypeSymbol.IsErrorType();
        public bool IsUnsafe() => TypeSymbol.IsUnsafe();
        public bool IsStatic => _extensions.IsStatic(_defaultType);
        public bool IsNullableTypeOrTypeParameter() => TypeSymbol.IsNullableTypeOrTypeParameter();
        public bool IsVoid => _extensions.IsVoid(_defaultType);
        public bool IsSZArray() => _extensions.IsSZArray(_defaultType);
        public TypeSymbolWithAnnotations GetNullableUnderlyingType() =>
            TypeSymbol.GetNullableUnderlyingTypeWithAnnotations();

        internal bool GetIsReferenceType(ConsList<TypeParameterSymbol> inProgress) =>
            _extensions.GetIsReferenceType(_defaultType, inProgress);
        internal bool GetIsValueType(ConsList<TypeParameterSymbol> inProgress) =>
            _extensions.GetIsValueType(_defaultType, inProgress);

        public string ToDisplayString(SymbolDisplayFormat format = null)
        {
            var str = !HasType ? "<null>" : TypeSymbol.ToDisplayString(format);
            if (format != null)
            {
                if (format.MiscellaneousOptions.IncludesOption(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier) &&
                    !IsNullableType() && !IsValueType &&
                    (NullableAnnotation == CSharpNullableAnnotation.Annotated ||
                     (NullableAnnotation == CSharpNullableAnnotation.Nullable && !TypeSymbol.IsTypeParameterDisallowingAnnotation())))
                {
                    return str + "?";
                }
                else if (format.CompilerInternalOptions.IncludesOption(SymbolDisplayCompilerInternalOptions.IncludeNonNullableTypeModifier) &&
                    !IsValueType &&
                    NullableAnnotation.IsAnyNotNullable() && !TypeSymbol.IsTypeParameterDisallowingAnnotation())
                {
                    return str + "!";
                }
            }
            return str;
        }

        internal string GetDebuggerDisplay() => !this.HasType ? "<null>" : ToDisplayString(DebuggerDisplayFormat);

        string IFormattable.ToString(string format, IFormatProvider formatProvider)
        {
            return ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        }

        public bool Equals(TypeSymbolWithAnnotations other, TypeCompareKind comparison)
        {
            if (this.IsSameAs(other))
            {
                return true;
            }

            if (!HasType)
            {
                if (other.HasType || NullableAnnotation != other.NullableAnnotation)
                    return false;
            }
            else if (!other.HasType || !TypeSymbolEquals(other, comparison))
            {
                return false;
            }

            // Make sure custom modifiers are the same.
            if ((comparison & TypeCompareKind.IgnoreCustomModifiersAndArraySizesAndLowerBounds) == 0 &&
                !this.CustomModifiers.SequenceEqual(other.CustomModifiers))
            {
                return false;
            }

            var thisAnnotation = NullableAnnotation;
            var otherAnnotation = other.NullableAnnotation;
            if (!HasType)
            {
                return thisAnnotation == otherAnnotation;
            }
            else if ((comparison & TypeCompareKind.IgnoreNullableModifiersForReferenceTypes) == 0)
            {
                if (otherAnnotation != thisAnnotation && (!TypeSymbol.IsValueType || TypeSymbol.IsNullableType()))
                {
                    if (thisAnnotation == CSharpNullableAnnotation.Unknown || otherAnnotation == CSharpNullableAnnotation.Unknown)
                    {
                        if ((comparison & TypeCompareKind.UnknownNullableModifierMatchesAny) == 0)
                        {
                            return false;
                        }
                    }
                    else if ((comparison & TypeCompareKind.IgnoreInsignificantNullableModifiersDifference) == 0)
                    {
                        return false;
                    }
                    else if (thisAnnotation.IsAnyNullable())
                    {
                        if (!otherAnnotation.IsAnyNullable())
                        {
                            return false;
                        }
                    }
                    else if (!otherAnnotation.IsAnyNullable())
                    {
                        Debug.Assert(thisAnnotation.IsAnyNotNullable());
                        Debug.Assert(otherAnnotation.IsAnyNotNullable());
                        if (TypeSymbol.IsPossiblyNullableReferenceTypeTypeParameter())
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal sealed class EqualsComparer : EqualityComparer<TypeSymbolWithAnnotations>
        {
            internal static readonly EqualsComparer Instance = new EqualsComparer();

            private EqualsComparer()
            {
            }

            public override int GetHashCode(TypeSymbolWithAnnotations obj)
            {
                if (!obj.HasType)
                {
                    return 0;
                }
                return obj.TypeSymbol.GetHashCode();
            }

            public override bool Equals(TypeSymbolWithAnnotations x, TypeSymbolWithAnnotations y)
            {
                if (!x.HasType)
                {
                    return !y.HasType;
                }
                return x.Equals(y, TypeCompareKind.ConsiderEverything);
            }
        }

        internal bool TypeSymbolEquals(TypeSymbolWithAnnotations other, TypeCompareKind comparison) =>
            _extensions.TypeSymbolEquals(this, other, comparison);

        public bool GetUnificationUseSiteDiagnosticRecursive(ref DiagnosticInfo result, Symbol owner, ref HashSet<TypeSymbol> checkedTypes)
        {
            return TypeSymbol.GetUnificationUseSiteDiagnosticRecursive(ref result, owner, ref checkedTypes) ||
                   Symbol.GetUnificationUseSiteDiagnosticRecursive(ref result, this.CustomModifiers, owner, ref checkedTypes);
        }

        public void CheckAllConstraints(CSharpCompilation compilation, ConversionsBase conversions, Location location, DiagnosticBag diagnostics)
        {
            TypeSymbol.CheckAllConstraints(compilation, conversions, location, diagnostics);
        }

        public bool IsAtLeastAsVisibleAs(Symbol sym, ref HashSet<DiagnosticInfo> useSiteDiagnostics)
        {
            // System.Nullable is public, so it is safe to delegate to the underlying.
            return NullableUnderlyingTypeOrSelf.IsAtLeastAsVisibleAs(sym, ref useSiteDiagnostics);
        }

        public TypeSymbolWithAnnotations SubstituteType(AbstractTypeMap typeMap) =>
            _extensions.SubstituteType(this, typeMap, withTupleUnification: false);
        public TypeSymbolWithAnnotations SubstituteTypeWithTupleUnification(AbstractTypeMap typeMap) =>
            _extensions.SubstituteType(this, typeMap, withTupleUnification: true);

        internal TypeSymbolWithAnnotations TransformToTupleIfCompatible() => _extensions.TransformToTupleIfCompatible(this);

        internal TypeSymbolWithAnnotations SubstituteTypeCore(AbstractTypeMap typeMap, bool withTupleUnification)
        {
            var newCustomModifiers = typeMap.SubstituteCustomModifiers(this.CustomModifiers);
            TypeSymbol typeSymbol = this.TypeSymbol;
            var newTypeWithModifiers = typeMap.SubstituteType(typeSymbol, withTupleUnification);

            if (!typeSymbol.IsTypeParameter())
            {
                Debug.Assert(newTypeWithModifiers.NullableAnnotation == CSharpNullableAnnotation.Unknown || (typeSymbol.IsNullableType() && newTypeWithModifiers.NullableAnnotation.IsAnyNullable()));
                Debug.Assert(newTypeWithModifiers.CustomModifiers.IsEmpty);

                if (typeSymbol.Equals(newTypeWithModifiers.TypeSymbol, TypeCompareKind.ConsiderEverything) &&
                    newCustomModifiers == CustomModifiers)
                {
                    return this; // substitution had no effect on the type or modifiers
                }
                else if ((NullableAnnotation == CSharpNullableAnnotation.Unknown || (typeSymbol.IsNullableType() && NullableAnnotation.IsAnyNullable())) &&
                    newCustomModifiers.IsEmpty)
                {
                    return newTypeWithModifiers;
                }

                return Create(newTypeWithModifiers.TypeSymbol, NullableAnnotation, newCustomModifiers);
            }

            if (newTypeWithModifiers.Is((TypeParameterSymbol)typeSymbol) &&
                newCustomModifiers == CustomModifiers)
            {
                return this; // substitution had no effect on the type or modifiers
            }
            else if (Is((TypeParameterSymbol)typeSymbol))
            {
                return newTypeWithModifiers;
            }

            CSharpNullableAnnotation newAnnotation;

            Debug.Assert(!IsIndexedTypeParameter(newTypeWithModifiers.TypeSymbol) || newTypeWithModifiers.NullableAnnotation == CSharpNullableAnnotation.Unknown);

            if (NullableAnnotation.IsAnyNullable() || newTypeWithModifiers.NullableAnnotation.IsAnyNullable())
            {
                newAnnotation = NullableAnnotation == CSharpNullableAnnotation.Annotated || newTypeWithModifiers.NullableAnnotation == CSharpNullableAnnotation.Annotated ?
                    CSharpNullableAnnotation.Annotated : CSharpNullableAnnotation.Nullable;
            }
            else if (IsIndexedTypeParameter(newTypeWithModifiers.TypeSymbol))
            {
                newAnnotation = NullableAnnotation;
            }
            else if (NullableAnnotation != CSharpNullableAnnotation.Unknown)
            {
                if (!typeSymbol.IsTypeParameterDisallowingAnnotation())
                {
                    newAnnotation = NullableAnnotation;
                }
                else
                {
                    newAnnotation = newTypeWithModifiers.NullableAnnotation;
                }
            }
            else if (newTypeWithModifiers.NullableAnnotation != CSharpNullableAnnotation.Unknown)
            {
                newAnnotation = newTypeWithModifiers.NullableAnnotation;
            }
            else
            {
                Debug.Assert(NullableAnnotation == CSharpNullableAnnotation.Unknown);
                Debug.Assert(newTypeWithModifiers.NullableAnnotation == CSharpNullableAnnotation.Unknown);
                newAnnotation = NullableAnnotation;
            }

            return CreateNonLazyType(
                newTypeWithModifiers.TypeSymbol,
                newAnnotation,
                newCustomModifiers.Concat(newTypeWithModifiers.CustomModifiers));
        }

        public void ReportDiagnosticsIfObsolete(Binder binder, SyntaxNode syntax, DiagnosticBag diagnostics) =>
            _extensions.ReportDiagnosticsIfObsolete(this, binder, syntax, diagnostics);

        internal bool TypeSymbolEqualsCore(TypeSymbolWithAnnotations other, TypeCompareKind comparison)
        {
            return TypeSymbol.Equals(other.TypeSymbol, comparison);
        }

        internal void ReportDiagnosticsIfObsoleteCore(Binder binder, SyntaxNode syntax, DiagnosticBag diagnostics)
        {
            binder.ReportDiagnosticsIfObsolete(diagnostics, TypeSymbol, syntax, hasBaseReceiver: false);
        }

        /// <summary>
        /// Extract type under assumption that there should be no custom modifiers or annotations.
        /// The method asserts otherwise.
        /// </summary>
        public TypeSymbol AsTypeSymbolOnly() => _extensions.AsTypeSymbolOnly(_defaultType);

        /// <summary>
        /// Is this the given type parameter?
        /// </summary>
        public bool Is(TypeParameterSymbol other)
        {
            return NullableAnnotation == CSharpNullableAnnotation.Unknown && ((object)_defaultType == other) &&
                   CustomModifiers.IsEmpty;
        }

        public TypeSymbolWithAnnotations WithTypeAndModifiers(TypeSymbol typeSymbol, ImmutableArray<CustomModifier> customModifiers) =>
            _extensions.WithTypeAndModifiers(this, typeSymbol, customModifiers);

        public bool NeedsNullableAttribute()
        {
            return NeedsNullableAttribute(this, typeOpt: null);
        }

        public static bool NeedsNullableAttribute(
            TypeSymbolWithAnnotations typeWithAnnotationsOpt,
            TypeSymbol typeOpt)
        {
            var type = TypeSymbolExtensions.VisitType(
                typeWithAnnotationsOpt,
                typeOpt,
                typeWithAnnotationsPredicateOpt: (t, a, b) => t.NullableAnnotation != CSharpNullableAnnotation.Unknown && !t.TypeSymbol.IsErrorType() && !t.TypeSymbol.IsValueType,
                typePredicateOpt: null,
                arg: (object)null);
            return (object)type != null;
        }

        public void AddNullableTransforms(ArrayBuilder<byte> transforms)
        {
            var typeSymbol = TypeSymbol;
            byte flag;

            if (NullableAnnotation == CSharpNullableAnnotation.Unknown || typeSymbol.IsValueType)
            {
                flag = (byte)CSharpNullableAnnotation.Unknown;
            }
            else if (NullableAnnotation.IsAnyNullable())
            {
                flag = (byte)CSharpNullableAnnotation.Annotated;
            }
            else
            {
                flag = (byte)CSharpNullableAnnotation.NotAnnotated;
            }

            transforms.Add(flag);
            typeSymbol.AddNullableTransforms(transforms);
        }

        public bool ApplyNullableTransforms(byte defaultTransformFlag, ImmutableArray<byte> transforms, ref int position, out TypeSymbolWithAnnotations result)
        {
            result = this;

            byte transformFlag;
            if (transforms.IsDefault)
            {
                transformFlag = defaultTransformFlag;
            }
            else if (position < transforms.Length)
            {
                transformFlag = transforms[position++];
            }
            else
            {
                return false;
            }

            TypeSymbol oldTypeSymbol = TypeSymbol;
            TypeSymbol newTypeSymbol;

            if (!oldTypeSymbol.ApplyNullableTransforms(defaultTransformFlag, transforms, ref position, out newTypeSymbol))
            {
                return false;
            }

            if ((object)oldTypeSymbol != newTypeSymbol)
            {
                result = result.WithTypeAndModifiers(newTypeSymbol, result.CustomModifiers);
            }

            switch ((CSharpNullableAnnotation)transformFlag)
            {
                case CSharpNullableAnnotation.Annotated:
                    result = result.AsNullableReferenceType();
                    break;

                case CSharpNullableAnnotation.NotAnnotated:
                    result = result.AsNotNullableReferenceType();
                    break;

                case CSharpNullableAnnotation.Unknown:
                    if (result.NullableAnnotation != CSharpNullableAnnotation.Unknown &&
                        !(result.NullableAnnotation.IsAnyNullable() && oldTypeSymbol.IsNullableType())) // Preserve nullable annotation on Nullable<T>.
                    {
                        result = CreateNonLazyType(newTypeSymbol, CSharpNullableAnnotation.Unknown, result.CustomModifiers);
                    }
                    break;

                default:
                    result = this;
                    return false;
            }

            return true;
        }

        public TypeSymbolWithAnnotations WithTopLevelNonNullability()
        {
            var typeSymbol = TypeSymbol;
            if (NullableAnnotation == CSharpNullableAnnotation.NotNullable || (typeSymbol.IsValueType && !typeSymbol.IsNullableType()))
            {
                return this;
            }

            return CreateNonLazyType(typeSymbol, CSharpNullableAnnotation.NotNullable, CustomModifiers);
        }

        public TypeSymbolWithAnnotations SetUnknownNullabilityForReferenceTypes()
        {
            var typeSymbol = TypeSymbol;

            if (NullableAnnotation != CSharpNullableAnnotation.Unknown)
            {
                if (!typeSymbol.IsValueType)
                {
                    typeSymbol = typeSymbol.SetUnknownNullabilityForReferenceTypes();

                    return CreateNonLazyType(typeSymbol, CSharpNullableAnnotation.Unknown, CustomModifiers);
                }
            }

            var newTypeSymbol = typeSymbol.SetUnknownNullabilityForReferenceTypes();

            if ((object)newTypeSymbol != typeSymbol)
            {
                return WithTypeAndModifiers(newTypeSymbol, CustomModifiers);
            }

            return this;
        }

        public TypeSymbolWithAnnotations SetSpeakableNullabilityForReferenceTypes()
        {
            if (!HasType)
            {
                return default;
            }

            var newTypeSymbol = TypeSymbol.SetSpeakableNullabilityForReferenceTypes();

            if (!NullableAnnotation.IsSpeakable())
            {
                if (newTypeSymbol.IsValueType)
                {
                    return Create(newTypeSymbol, customModifiers: CustomModifiers);
                }

                return CreateNonLazyType(newTypeSymbol, NullableAnnotation.AsSpeakable(newTypeSymbol), CustomModifiers);
            }

            if ((object)newTypeSymbol != TypeSymbol)
            {
                return WithTypeAndModifiers(newTypeSymbol, CustomModifiers);
            }

            return this;
        }

#pragma warning disable CS0809
        [Obsolete("Unsupported", error: true)]
        public override bool Equals(object other)
#pragma warning restore CS0809
        {
            throw ExceptionUtilities.Unreachable;
        }

#pragma warning disable CS0809
        [Obsolete("Unsupported", error: true)]
        public override int GetHashCode()
#pragma warning restore CS0809
        {
            if (!HasType)
            {
                return 0;
            }
            return TypeSymbol.GetHashCode();
        }

#pragma warning disable CS0809
        [Obsolete("Unsupported", error: true)]
        public static bool operator ==(TypeSymbolWithAnnotations? x, TypeSymbolWithAnnotations? y)
#pragma warning restore CS0809
        {
            throw ExceptionUtilities.Unreachable;
        }

#pragma warning disable CS0809
        [Obsolete("Unsupported", error: true)]
        public static bool operator !=(TypeSymbolWithAnnotations? x, TypeSymbolWithAnnotations? y)
#pragma warning restore CS0809
        {
            throw ExceptionUtilities.Unreachable;
        }

        // Field-wise ReferenceEquals.
        internal bool IsSameAs(TypeSymbolWithAnnotations other)
        {
            return ReferenceEquals(_defaultType, other._defaultType) &&
                NullableAnnotation == other.NullableAnnotation &&
                ReferenceEquals(_extensions, other._extensions);
        }

        /// <summary>
        /// Additional data or behavior beyond the core TypeSymbolWithAnnotations.
        /// </summary>
        private abstract class Extensions
        {
            internal static readonly Extensions Default = new NonLazyType(ImmutableArray<CustomModifier>.Empty);

            internal static Extensions Create(ImmutableArray<CustomModifier> customModifiers)
            {
                if (customModifiers.IsEmpty)
                {
                    return Default;
                }
                return new NonLazyType(customModifiers);
            }

            internal static Extensions CreateLazy(CSharpCompilation compilation, TypeSymbolWithAnnotations underlying)
            {
                return new LazyNullableTypeParameter(compilation, underlying);
            }

            internal abstract TypeSymbol GetResolvedType(TypeSymbol defaultType);
            internal abstract ImmutableArray<CustomModifier> CustomModifiers { get; }

            internal abstract TypeSymbolWithAnnotations AsNullableReferenceType(TypeSymbolWithAnnotations type);
            internal abstract TypeSymbolWithAnnotations AsNotNullableReferenceType(TypeSymbolWithAnnotations type);

            internal abstract TypeSymbolWithAnnotations WithModifiers(TypeSymbolWithAnnotations type, ImmutableArray<CustomModifier> customModifiers);

            internal abstract TypeSymbol GetNullableUnderlyingTypeOrSelf(TypeSymbol typeSymbol);

            internal abstract bool GetIsReferenceType(TypeSymbol typeSymbol, ConsList<TypeParameterSymbol> inProgress);
            internal abstract bool GetIsValueType(TypeSymbol typeSymbol, ConsList<TypeParameterSymbol> inProgress);

            internal abstract TypeSymbol AsTypeSymbolOnly(TypeSymbol typeSymbol);

            internal abstract SpecialType GetSpecialType(TypeSymbol typeSymbol);
            internal abstract bool IsRestrictedType(TypeSymbol typeSymbol, bool ignoreSpanLikeTypes);
            internal abstract bool IsStatic(TypeSymbol typeSymbol);
            internal abstract bool IsVoid(TypeSymbol typeSymbol);
            internal abstract bool IsSZArray(TypeSymbol typeSymbol);

            internal abstract TypeSymbolWithAnnotations WithTypeAndModifiers(TypeSymbolWithAnnotations type, TypeSymbol typeSymbol, ImmutableArray<CustomModifier> customModifiers);

            internal abstract bool TypeSymbolEquals(TypeSymbolWithAnnotations type, TypeSymbolWithAnnotations other, TypeCompareKind comparison);
            internal abstract TypeSymbolWithAnnotations SubstituteType(TypeSymbolWithAnnotations type, AbstractTypeMap typeMap, bool withTupleUnification);
            internal abstract TypeSymbolWithAnnotations TransformToTupleIfCompatible(TypeSymbolWithAnnotations type);
            internal abstract void ReportDiagnosticsIfObsolete(TypeSymbolWithAnnotations type, Binder binder, SyntaxNode syntax, DiagnosticBag diagnostics);
        }

        private sealed class NonLazyType : Extensions
        {
            private readonly ImmutableArray<CustomModifier> _customModifiers;

            public NonLazyType(ImmutableArray<CustomModifier> customModifiers)
            {
                Debug.Assert(!customModifiers.IsDefault);
                _customModifiers = customModifiers;
            }

            internal override TypeSymbol GetResolvedType(TypeSymbol defaultType) => defaultType;
            internal override ImmutableArray<CustomModifier> CustomModifiers => _customModifiers;

            internal override SpecialType GetSpecialType(TypeSymbol typeSymbol) => typeSymbol.SpecialType;
            internal override bool IsRestrictedType(TypeSymbol typeSymbol, bool ignoreSpanLikeTypes) => typeSymbol.IsRestrictedType(ignoreSpanLikeTypes);
            internal override bool IsStatic(TypeSymbol typeSymbol) => typeSymbol.IsStatic;
            internal override bool IsVoid(TypeSymbol typeSymbol) => typeSymbol.SpecialType == SpecialType.System_Void;
            internal override bool IsSZArray(TypeSymbol typeSymbol) => typeSymbol.IsSZArray();

            internal override TypeSymbol GetNullableUnderlyingTypeOrSelf(TypeSymbol typeSymbol) => typeSymbol.StrippedType();

            internal override bool GetIsReferenceType(TypeSymbol typeSymbol, ConsList<TypeParameterSymbol> inProgress)
            {
                if (typeSymbol.TypeKind == TypeKind.TypeParameter)
                {
                    return ((TypeParameterSymbol)typeSymbol).GetIsReferenceType(inProgress);
                }
                return typeSymbol.IsReferenceType;
            }

            internal override bool GetIsValueType(TypeSymbol typeSymbol, ConsList<TypeParameterSymbol> inProgress)
            {
                if (typeSymbol.TypeKind == TypeKind.TypeParameter)
                {
                    return ((TypeParameterSymbol)typeSymbol).GetIsValueType(inProgress);
                }
                return typeSymbol.IsValueType;
            }

            internal override TypeSymbolWithAnnotations WithModifiers(TypeSymbolWithAnnotations type, ImmutableArray<CustomModifier> customModifiers)
            {
                return CreateNonLazyType(type._defaultType, type.NullableAnnotation, customModifiers);
            }

            internal override TypeSymbol AsTypeSymbolOnly(TypeSymbol typeSymbol) => typeSymbol;

            internal override TypeSymbolWithAnnotations WithTypeAndModifiers(TypeSymbolWithAnnotations type, TypeSymbol typeSymbol, ImmutableArray<CustomModifier> customModifiers)
            {
                return CreateNonLazyType(typeSymbol, type.NullableAnnotation, customModifiers);
            }

            internal override TypeSymbolWithAnnotations AsNullableReferenceType(TypeSymbolWithAnnotations type)
            {
                return CreateNonLazyType(type._defaultType, CSharpNullableAnnotation.Annotated, _customModifiers);
            }

            internal override TypeSymbolWithAnnotations AsNotNullableReferenceType(TypeSymbolWithAnnotations type)
            {
                var defaultType = type._defaultType;
                return CreateNonLazyType(defaultType, defaultType.IsNullableType() ? type.NullableAnnotation : CSharpNullableAnnotation.NotAnnotated, _customModifiers);
            }

            internal override bool TypeSymbolEquals(TypeSymbolWithAnnotations type, TypeSymbolWithAnnotations other, TypeCompareKind comparison)
            {
                return type.TypeSymbolEqualsCore(other, comparison);
            }

            internal override TypeSymbolWithAnnotations SubstituteType(TypeSymbolWithAnnotations type, AbstractTypeMap typeMap, bool withTupleUnification)
            {
                return type.SubstituteTypeCore(typeMap, withTupleUnification);
            }

            internal override TypeSymbolWithAnnotations TransformToTupleIfCompatible(TypeSymbolWithAnnotations type)
            {
                var defaultType = type._defaultType;
                var transformedType = TupleTypeSymbol.TransformToTupleIfCompatible(defaultType);
                if ((object)defaultType != transformedType)
                {
                    return TypeSymbolWithAnnotations.Create(transformedType, type.NullableAnnotation, _customModifiers);
                }
                return type;
            }

            internal override void ReportDiagnosticsIfObsolete(TypeSymbolWithAnnotations type, Binder binder, SyntaxNode syntax, DiagnosticBag diagnostics)
            {
                type.ReportDiagnosticsIfObsoleteCore(binder, syntax, diagnostics);
            }
        }

        /// <summary>
        /// Nullable type parameter. The underlying TypeSymbol is resolved
        /// lazily to avoid cycles when binding declarations.
        /// </summary>
        private sealed class LazyNullableTypeParameter : Extensions
        {
            private readonly CSharpCompilation _compilation;
            private readonly TypeSymbolWithAnnotations _underlying;
            private TypeSymbol _resolved;

            public LazyNullableTypeParameter(CSharpCompilation compilation, TypeSymbolWithAnnotations underlying)
            {
                Debug.Assert(!underlying.NullableAnnotation.IsAnyNullable());
                Debug.Assert(underlying.TypeKind == TypeKind.TypeParameter);
                Debug.Assert(underlying.CustomModifiers.IsEmpty);
                _compilation = compilation;
                _underlying = underlying;
            }

            internal override bool IsVoid(TypeSymbol typeSymbol) => false;
            internal override bool IsSZArray(TypeSymbol typeSymbol) => false;
            internal override bool IsStatic(TypeSymbol typeSymbol) => false;

            private TypeSymbol GetResolvedType()
            {
                if ((object)_resolved == null)
                {
                    if (!_underlying.IsValueType)
                    {
                        _resolved = _underlying.TypeSymbol;
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref _resolved,
                            _compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(ImmutableArray.Create(_underlying)),
                            null);
                    }
                }

                return _resolved;
            }

            internal override bool GetIsReferenceType(TypeSymbol typeSymbol, ConsList<TypeParameterSymbol> inProgress)
            {
                return _underlying.GetIsReferenceType(inProgress);
            }

            internal override bool GetIsValueType(TypeSymbol typeSymbol, ConsList<TypeParameterSymbol> inProgress)
            {
                return _underlying.GetIsValueType(inProgress);
            }

            internal override TypeSymbol GetNullableUnderlyingTypeOrSelf(TypeSymbol typeSymbol) => _underlying.TypeSymbol;

            internal override SpecialType GetSpecialType(TypeSymbol typeSymbol)
            {
                var specialType = _underlying.SpecialType;
                return specialType.IsValueType() ? SpecialType.None : specialType;
            }

            internal override bool IsRestrictedType(TypeSymbol typeSymbol, bool ignoreSpanLikeTypes) => _underlying.IsRestrictedType(ignoreSpanLikeTypes);

            internal override TypeSymbol AsTypeSymbolOnly(TypeSymbol typeSymbol)
            {
                var resolvedType = GetResolvedType();
                Debug.Assert(resolvedType.IsNullableType() && CustomModifiers.IsEmpty);
                return resolvedType;
            }

            internal override TypeSymbol GetResolvedType(TypeSymbol defaultType) => GetResolvedType();
            internal override ImmutableArray<CustomModifier> CustomModifiers => ImmutableArray<CustomModifier>.Empty;

            internal override TypeSymbolWithAnnotations WithModifiers(TypeSymbolWithAnnotations type, ImmutableArray<CustomModifier> customModifiers)
            {
                if (customModifiers.IsEmpty)
                {
                    return type;
                }

                var resolvedType = GetResolvedType();
                if (resolvedType.IsNullableType())
                {
                    return TypeSymbolWithAnnotations.Create(resolvedType, type.NullableAnnotation, customModifiers: customModifiers);
                }

                return CreateNonLazyType(resolvedType, type.NullableAnnotation, customModifiers);
            }

            internal override TypeSymbolWithAnnotations WithTypeAndModifiers(TypeSymbolWithAnnotations type, TypeSymbol typeSymbol, ImmutableArray<CustomModifier> customModifiers)
            {
                if (typeSymbol.IsNullableType())
                {
                    return TypeSymbolWithAnnotations.Create(typeSymbol, type.NullableAnnotation, customModifiers: customModifiers);
                }

                return CreateNonLazyType(typeSymbol, type.NullableAnnotation, customModifiers);
            }

            internal override TypeSymbolWithAnnotations AsNullableReferenceType(TypeSymbolWithAnnotations type)
            {
                return type;
            }

            internal override TypeSymbolWithAnnotations AsNotNullableReferenceType(TypeSymbolWithAnnotations type)
            {
                if (!_underlying.TypeSymbol.IsValueType)
                {
                    return _underlying;
                }
                return type;
            }

            internal override TypeSymbolWithAnnotations SubstituteType(TypeSymbolWithAnnotations type, AbstractTypeMap typeMap, bool withTupleUnification)
            {
                if ((object)_resolved != null)
                {
                    return type.SubstituteTypeCore(typeMap, withTupleUnification);
                }

                var newUnderlying = _underlying.SubstituteTypeCore(typeMap, withTupleUnification);
                if (!newUnderlying.IsSameAs(this._underlying))
                {
                    if ((newUnderlying.TypeSymbol.Equals(this._underlying.TypeSymbol, TypeCompareKind.ConsiderEverything) ||
                            newUnderlying.TypeSymbol is IndexedTypeParameterSymbolForOverriding) &&
                        newUnderlying.CustomModifiers.IsEmpty)
                    {
                        return CreateLazyNullableType(_compilation, newUnderlying);
                    }

                    return type.SubstituteTypeCore(typeMap, withTupleUnification);
                }
                else
                {
                    return type; // substitution had no effect on the type or modifiers
                }
            }

            internal override TypeSymbolWithAnnotations TransformToTupleIfCompatible(TypeSymbolWithAnnotations type)
            {
                return type;
            }

            internal override void ReportDiagnosticsIfObsolete(TypeSymbolWithAnnotations type, Binder binder, SyntaxNode syntax, DiagnosticBag diagnostics)
            {
                if ((object)_resolved != null)
                {
                    type.ReportDiagnosticsIfObsoleteCore(binder, syntax, diagnostics);
                }
                else
                {
                    diagnostics.Add(new LazyObsoleteDiagnosticInfo(type, binder.ContainingMemberOrLambda, binder.Flags), syntax.GetLocation());
                }
            }

            internal override bool TypeSymbolEquals(TypeSymbolWithAnnotations type, TypeSymbolWithAnnotations other, TypeCompareKind comparison)
            {
                var otherLazy = other._extensions as LazyNullableTypeParameter;

                if ((object)otherLazy != null)
                {
                    return _underlying.TypeSymbolEquals(otherLazy._underlying, comparison);
                }

                return type.TypeSymbolEqualsCore(other, comparison);
            }
        }

        /// <summary>
        /// Compute the flow state resulting from reading from an lvalue.
        /// </summary>
        internal TypeWithState ToTypeWithState()
        {
            // This operation reflects reading from an lvalue, which produces an rvalue.
            // Reading from a variable of a type parameter (that could be substituted with a nullable type), but which
            // cannot itself be annotated (because it isn't known to be a reference type), may yield a null value
            // even though the type parameter isn't annotated.
            return new TypeWithState(
                this.TypeSymbol,
                IsPossiblyNullableTypeTypeParameter() || this.NullableAnnotation.IsAnyNullable() ? NullableFlowState.MaybeNull : NullableFlowState.NotNull);
        }
    }
}
