using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace Microsoft.PowerShell.CoreCLR
{

    // Licensed to the .NET Foundation under one or more agreements.
    // The .NET Foundation licenses this file to you under the MIT license.
    // See the LICENSE file in the project root for more information.
    internal class DisassemblingGenericContext
    {
        public DisassemblingGenericContext(ImmutableArray<string> typeParameters, ImmutableArray<string> methodParameters)
        {
            MethodParameters = methodParameters;
            TypeParameters = typeParameters;
        }

        public ImmutableArray<string> MethodParameters { get; }
        public ImmutableArray<string> TypeParameters { get; }
    }

    // Test implementation of ISignatureTypeProvider<TType, TGenericContext> that uses strings in ilasm syntax as TType.
    // A real provider in any sort of perf constraints would not want to allocate strings freely like this, but it keeps test code simple.
    internal class DisassemblingTypeProvider : ISignatureTypeProvider<string, DisassemblingGenericContext>
    {
        public virtual string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
               
            switch (typeCode)
            {
                
                case PrimitiveTypeCode.Boolean:
                    return "System.Boolean";

                case PrimitiveTypeCode.Byte:
                    return "System.Byte";

                case PrimitiveTypeCode.Char:
                    return "System.Char";

                case PrimitiveTypeCode.Double:
                    return "System.Double";

                case PrimitiveTypeCode.Int16:
                    return "System.Int16";

                case PrimitiveTypeCode.Int32:
                    return "System.Int32";

                case PrimitiveTypeCode.Int64:
                    return "System.Int64";

                case PrimitiveTypeCode.IntPtr:
                    return "System.IntPtr";

                case PrimitiveTypeCode.Object:
                    return "System.Object";

                case PrimitiveTypeCode.SByte:
                    return "System.Int8";

                case PrimitiveTypeCode.Single:
                    return "System.Single";

                case PrimitiveTypeCode.String:
                    return "System.String";
                case PrimitiveTypeCode.TypedReference:
                    throw new NotSupportedException("Don't know how to handle PrimitiveTypeCode.TypedReference");

                case PrimitiveTypeCode.UInt16:
                    return "System.UInt16";

                case PrimitiveTypeCode.UInt32:
                    return "System.UInt32";

                case PrimitiveTypeCode.UInt64:
                    return "System.UInt64";

                case PrimitiveTypeCode.UIntPtr:
                    return "System.UIntPtr";

                case PrimitiveTypeCode.Void:
                    return "System.Void";

                default:
                    Debug.Assert(false);
                    throw new ArgumentOutOfRangeException(nameof(typeCode));
            }            
        }

        public virtual string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind = 0)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);

            string name = definition.Namespace.IsNil
                ? reader.GetString(definition.Name)
                : reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name);            
            
            if (definition.Attributes.IsNested())
            {
                TypeDefinitionHandle declaringTypeHandle = definition.GetDeclaringType();
                return GetTypeFromDefinition(reader, declaringTypeHandle, 0) + "/" + name;
            }

            return name;
        }

        public virtual string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind = 0)
        {
            TypeReference reference = reader.GetTypeReference(handle);
            Handle scope = reference.ResolutionScope;

            string name = reference.Namespace.IsNil
                ? reader.GetString(reference.Name)
                : reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);

            switch (scope.Kind)
            {
                case HandleKind.ModuleReference:
                    return "[.module  " + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)scope).Name) + "]" + name;

                case HandleKind.AssemblyReference:
                    //var assemblyReferenceHandle = (AssemblyReferenceHandle)scope;
                    //var assemblyReference = reader.GetAssemblyReference(assemblyReferenceHandle);
                    //return "[" + reader.GetString(assemblyReference.Name) + "]" + name;
                    return name;

                case HandleKind.TypeReference:
                    return GetTypeFromReference(reader, (TypeReferenceHandle)scope) + "/" + name;

                default:
                    // rare cases:  ModuleDefinition means search within defs of current module (used by WinMDs for projections)
                    //              nil means search exported types of same module (haven't seen this in practice). For the test
                    //              purposes here, it's sufficient to format both like defs.
                    Debug.Assert(scope == Handle.ModuleDefinition || scope.IsNil);
                    return name;
            }
        }

        public virtual string GetTypeFromSpecification(MetadataReader reader, DisassemblingGenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind = 0)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }

        public virtual string GetSZArrayType(string elementType)
        {
            return elementType + "[]";
        }

        public virtual string GetPointerType(string elementType)
        {
            return elementType + "*";
        }

        public virtual string GetByReferenceType(string elementType)
        {
            return elementType + "&";
        }

        public virtual string GetGenericMethodParameter(DisassemblingGenericContext genericContext, int index)
        {
            return "!" + genericContext.MethodParameters[index];
        }

        public virtual string GetGenericTypeParameter(DisassemblingGenericContext genericContext, int index)
        {
            return "!!" + genericContext.TypeParameters[index];
        }

        public virtual string GetPinnedType(string elementType)
        {
            return elementType + " pinned";
        }

        public virtual string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            return genericType + "[" + String.Join(",", typeArguments) + "]";
        }

        public virtual string GetArrayType(string elementType, ArrayShape shape)
        {
            var builder = new StringBuilder();

            builder.Append(elementType);
            builder.Append('[');

            for (int i = 0; i < shape.Rank; i++)
            {
                int lowerBound = 0;

                if (i < shape.LowerBounds.Length)
                {
                    lowerBound = shape.LowerBounds[i];
                    builder.Append(lowerBound);
                }

                builder.Append("...");

                if (i < shape.Sizes.Length)
                {
                    builder.Append(lowerBound + shape.Sizes[i] - 1);
                }

                if (i < shape.Rank - 1)
                {
                    builder.Append(',');
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        public virtual string GetTypeFromHandle(MetadataReader reader, DisassemblingGenericContext genericContext, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle);

                case HandleKind.TypeReference:
                    return GetTypeFromReference(reader, (TypeReferenceHandle)handle);

                case HandleKind.TypeSpecification:
                    return GetTypeFromSpecification(reader, genericContext, (TypeSpecificationHandle)handle);

                default:
                    throw new ArgumentOutOfRangeException(nameof(handle));
            }
        }

        public virtual string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired)
        {
            return unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifierType + ")";
        }

        public virtual string GetFunctionPointerType(MethodSignature<string> signature)
        {
            ImmutableArray<string> parameterTypes = signature.ParameterTypes;

            int requiredParameterCount = signature.RequiredParameterCount;

            var builder = new StringBuilder();
            builder.Append("method ");
            builder.Append(signature.ReturnType);
            builder.Append(" *(");

            int i;
            for (i = 0; i < requiredParameterCount; i++)
            {
                builder.Append(parameterTypes[i]);
                if (i < parameterTypes.Length - 1)
                {
                    builder.Append(", ");
                }
            }

            if (i < parameterTypes.Length)
            {
                builder.Append("..., ");
                for (; i < parameterTypes.Length; i++)
                {
                    builder.Append(parameterTypes[i]);
                    if (i < parameterTypes.Length - 1)
                    {
                        builder.Append(", ");
                    }
                }
            }

            builder.Append(')');
            return builder.ToString();
        }
    }

    static class TypeAttributeExtensions
    {

        // This mask is the fastest way to check if a type is nested from its flags,
        // but it should not be added to the BCL enum as its semantics can be misleading.
        // Consider, for example, that (NestedFamANDAssem & NestedMask) == NestedFamORAssem.
        // Only comparison of the masked value to 0 is meaningful, which is different from
        // the other masks in the enum.
        private const TypeAttributes NestedMask = (TypeAttributes)0x00000006;

        public static bool IsNested(this TypeAttributes flags)
        {
            return (flags & NestedMask) != 0;
        }
    }
}