using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

using Focks.Extensions;
using Focks.Helpers;
using Mono.Reflection;

namespace Focks.IL
{
    internal class MethodRewriter
    {
        private MethodBase _method;
        private Shim _shim;

        private MethodRewriter() { }

        public static MethodRewriter CreateRewriter(MethodBase method)
        {
            return new MethodRewriter { _method = method };
        }

        public static MethodRewriter CreateRewriter(Shim shim)
        {
            return new MethodRewriter { _method = shim.Replacement.Method, _shim = shim };
        }

        public DynamicMethod RewriteMethodSignature(Shim[] shims)
        {
            ParameterInfo[] parameters = _method.GetParameters();
            List<Type> parameterTypes = new List<Type>();

            if (_shim == null)
            {
                if (!_method.IsStatic)
                    parameterTypes.Add(_method.DeclaringType);
            }
            else
            {
                if (!_shim.Original.IsStatic)
                    parameterTypes.Add(_shim.Original.DeclaringType);
            }

            parameterTypes.AddRange(parameters.Select(p => p.ParameterType));

            if (_shim != null)
            {
                parameterTypes.Add(_method.DeclaringType);
            }
            else
            {
                foreach (var shim in shims)
                    parameterTypes.Add(shim.Replacement.Method.DeclaringType);
            }

            Type returnType = _method.IsConstructor ? _method.DeclaringType : (_method as MethodInfo).ReturnType;
            return new DynamicMethod(_method.Name,
                returnType,
                parameterTypes.ToArray());
        }

        public DynamicMethod RewriteMethodBody(DynamicMethod dynamicMethod, Dictionary<string, DynamicMethod> signatures, Shim[] shims)
        {
            MethodDisassembler disassembler = new MethodDisassembler(_method);
            IList<LocalVariableInfo> locals = _method.GetMethodBody().LocalVariables;
            ILGenerator ilGenerator = dynamicMethod.GetILGenerator();

            var instructions = disassembler.GetILInstructions();
            Dictionary<int, Label> targetInstructions = new Dictionary<int, Label>();

            foreach (var local in locals)
                ilGenerator.DeclareLocal(local.LocalType, local.IsPinned);

            var ifTargets = instructions
                .Where(i => (i.Operand as Instruction) != null)
                .Select(i => (i.Operand as Instruction));

            foreach (Instruction instruction in ifTargets)
                targetInstructions.TryAdd(instruction.Offset, ilGenerator.DefineLabel());

            var switchTargets = instructions
                .Where(i => (i.Operand as Instruction[]) != null)
                .Select(i => (i.Operand as Instruction[]));

            foreach (Instruction[] _instructions in switchTargets)
            {
                foreach (Instruction _instruction in _instructions)
                    targetInstructions.TryAdd(_instruction.Offset, ilGenerator.DefineLabel());
            }

            foreach (var instruction in instructions)
            {
                if (targetInstructions.TryGetValue(instruction.Offset, out Label label))
                    ilGenerator.MarkLabel(label);

                switch (instruction.OpCode.OperandType)
                {
                    case OperandType.InlineNone:
                        if (_shim != null)
                        {
                            if (instruction.OpCode == OpCodes.Ldarg_0)
                                ilGenerator.Emit(OpCodes.Ldarg, (short)dynamicMethod.GetParameters().Count() - 1);
                            else if (instruction.OpCode == OpCodes.Ldarg_1)
                                ilGenerator.Emit(OpCodes.Ldarg, (short)0);
                            else if (instruction.OpCode == OpCodes.Ldarg_2)
                                ilGenerator.Emit(OpCodes.Ldarg, (short)1);
                            else if (instruction.OpCode == OpCodes.Ldarg_3)
                                ilGenerator.Emit(OpCodes.Ldarg, (short)2);
                            else
                                ilGenerator.Emit(instruction.OpCode);
                        }
                        else
                            ilGenerator.Emit(instruction.OpCode);
                        break;
                    case OperandType.InlineI:
                        ilGenerator.Emit(instruction.OpCode, (int)instruction.Operand);
                        break;
                    case OperandType.InlineI8:
                        ilGenerator.Emit(instruction.OpCode, (long)instruction.Operand);
                        break;
                    case OperandType.ShortInlineI:
                        if (instruction.OpCode == OpCodes.Ldc_I4_S)
                            ilGenerator.Emit(instruction.OpCode, (sbyte)instruction.Operand);
                        else
                            ilGenerator.Emit(instruction.OpCode, (byte)instruction.Operand);
                        break;
                    case OperandType.InlineR:
                        ilGenerator.Emit(instruction.OpCode, (double)instruction.Operand);
                        break;
                    case OperandType.ShortInlineR:
                        ilGenerator.Emit(instruction.OpCode, (float)instruction.Operand);
                        break;
                    case OperandType.InlineString:
                        ilGenerator.Emit(instruction.OpCode, (string)instruction.Operand);
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.InlineBrTarget:
                        Label targetLabel = targetInstructions[(instruction.Operand as Instruction).Offset];
                        ilGenerator.Emit(instruction.OpCode, targetLabel);
                        break;
                    case OperandType.InlineSwitch:
                        Instruction[] switchInstructions = (Instruction[])instruction.Operand;
                        Label[] targetLabels = new Label[switchInstructions.Length];
                        for (int i = 0; i < switchInstructions.Length; i++)
                            targetLabels[i] = targetInstructions[switchInstructions[i].Offset];
                        ilGenerator.Emit(instruction.OpCode, targetLabels);
                        break;
                    case OperandType.ShortInlineVar:
                    case OperandType.InlineVar:
                        int index = 0;
                        if (instruction.OpCode.Name.Contains("loc"))
                            index = ((LocalVariableInfo)instruction.Operand).LocalIndex;
                        else
                        {
                            index = ((ParameterInfo)instruction.Operand).Position;
                            index = _method.IsStatic ? index : index + 1;
                            if (_shim != null)
                                index = index == 0 ? dynamicMethod.GetParameters().Count() - 1 : index - 1;
                        }

                        if (instruction.OpCode.OperandType == OperandType.ShortInlineVar)
                            ilGenerator.Emit(instruction.OpCode, (byte)index);
                        else
                            ilGenerator.Emit(instruction.OpCode, (short)index);
                        break;
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                        MemberInfo memberInfo = (MemberInfo)instruction.Operand;
                        if (memberInfo.MemberType == MemberTypes.Field)
                        {
                            FieldInfo fieldInfo = (MemberInfo)instruction.Operand as FieldInfo;
                            ilGenerator.Emit(instruction.OpCode, fieldInfo);
                        }
                        else if (memberInfo.MemberType == MemberTypes.TypeInfo
                            || memberInfo.MemberType == MemberTypes.NestedType)
                        {
                            TypeInfo typeInfo = (MemberInfo)instruction.Operand as TypeInfo;
                            ilGenerator.Emit(instruction.OpCode, typeInfo);
                        }
                        else if (memberInfo is MethodBase)
                        {
                            MethodBase methodBase = (MemberInfo)instruction.Operand as MethodBase;
                            if (signatures.TryGetValue(methodBase.ToFullString(), out DynamicMethod dynMethod))
                            {
                                if (_shim == null)
                                {
                                    if (!methodBase.HasShim(shims))
                                    {
                                        int parameterCount = dynamicMethod.GetParameters().Count();
                                        int startIndex = parameterCount - shims.Length;
                                        for (int i = startIndex; i < parameterCount; i++)
                                            ilGenerator.Emit(OpCodes.Ldarg, (short)i);
                                    }
                                    else
                                    {
                                        int shimIndex = shims.Select(s => s.Original)
                                            .Select(m => m.ToFullString())
                                            .ToList().IndexOf(methodBase.ToFullString());
                                        int parameterCount = dynamicMethod.GetParameters().Count();
                                        int countDiff = parameterCount - shims.Length;
                                        ilGenerator.Emit(OpCodes.Ldarg, (short)(countDiff + shimIndex));
                                    }
                                }
                                ilGenerator.Emit(instruction.OpCode, dynMethod);
                            }
                            else
                            {
                                if (methodBase.IsConstructor)
                                    ilGenerator.Emit(instruction.OpCode, methodBase as ConstructorInfo);
                                else
                                    ilGenerator.Emit(instruction.OpCode, methodBase as MethodInfo);
                            }
                        }
                        else
                        {
                            throw new NotSupportedException();
                        }
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }

            return dynamicMethod;
        }

        public MethodInfo Rewrite()
        {
            MethodBody methodBody = _method.GetMethodBody();
            if (methodBody == null)
                return _method as MethodInfo;

            List<Type> parameterTypes = new List<Type>();
            if (!_method.IsStatic)
                parameterTypes.Add(_method.DeclaringType);

            parameterTypes.AddRange(_method.GetParameters().Select(p => p.ParameterType));
            DynamicMethod dynamicMethod = new DynamicMethod(
                _method.Name,
                (_method as MethodInfo).ReturnType,
                parameterTypes.ToArray());

            MethodDisassembler disassembler = new MethodDisassembler(_method);
            IList<LocalVariableInfo> locals = methodBody.LocalVariables;
            ILGenerator ilGenerator = dynamicMethod.GetILGenerator();

            var instructions = disassembler.GetILInstructions();
            Dictionary<int, Label> targetInstructions = new Dictionary<int, Label>();

            foreach (var local in locals)
                ilGenerator.DeclareLocal(local.LocalType, local.IsPinned);

            var ifTargets = instructions
                .Where(i => (i.Operand as Instruction) != null)
                .Select(i => (i.Operand as Instruction));

            foreach (Instruction instruction in ifTargets)
                targetInstructions.TryAdd(instruction.Offset, ilGenerator.DefineLabel());

            var switchTargets = instructions
                .Where(i => (i.Operand as Instruction[]) != null)
                .Select(i => (i.Operand as Instruction[]));

            foreach (Instruction[] _instructions in switchTargets)
            {
                foreach (Instruction _instruction in _instructions)
                    targetInstructions.TryAdd(_instruction.Offset, ilGenerator.DefineLabel());
            }

            foreach (var instruction in instructions)
            {
                if (targetInstructions.TryGetValue(instruction.Offset, out Label label))
                    ilGenerator.MarkLabel(label);

                switch (instruction.OpCode.OperandType)
                {
                    case OperandType.InlineNone:
                        ilGenerator.Emit(instruction.OpCode);
                        break;
                    case OperandType.InlineI:
                        ilGenerator.Emit(instruction.OpCode, (int)instruction.Operand);
                        break;
                    case OperandType.InlineI8:
                        ilGenerator.Emit(instruction.OpCode, (long)instruction.Operand);
                        break;
                    case OperandType.ShortInlineI:
                        if (instruction.OpCode == OpCodes.Ldc_I4_S)
                            ilGenerator.Emit(instruction.OpCode, (sbyte)instruction.Operand);
                        else
                            ilGenerator.Emit(instruction.OpCode, (byte)instruction.Operand);
                        break;
                    case OperandType.InlineR:
                        ilGenerator.Emit(instruction.OpCode, (double)instruction.Operand);
                        break;
                    case OperandType.ShortInlineR:
                        ilGenerator.Emit(instruction.OpCode, (float)instruction.Operand);
                        break;
                    case OperandType.InlineString:
                        ilGenerator.Emit(instruction.OpCode, (string)instruction.Operand);
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.InlineBrTarget:
                        Label targetLabel = targetInstructions[(instruction.Operand as Instruction).Offset];
                        ilGenerator.Emit(instruction.OpCode, targetLabel);
                        break;
                    case OperandType.InlineSwitch:
                        Instruction[] switchInstructions = (Instruction[])instruction.Operand;
                        Label[] targetLabels = new Label[switchInstructions.Length];
                        for (int i = 0; i < switchInstructions.Length; i++)
                            targetLabels[i] = targetInstructions[switchInstructions[i].Offset];
                        ilGenerator.Emit(instruction.OpCode, targetLabels);
                        break;
                    case OperandType.ShortInlineVar:
                    case OperandType.InlineVar:
                        int index = 0;
                        if (instruction.OpCode.Name.Contains("loc"))
                            index = ((LocalVariableInfo)instruction.Operand).LocalIndex;
                        else
                        {
                            index = ((ParameterInfo)instruction.Operand).Position;
                            index = _method.IsStatic ? index : index + 1;
                        }

                        if (instruction.OpCode.OperandType == OperandType.ShortInlineVar)
                            ilGenerator.Emit(instruction.OpCode, (byte)index);
                        else
                            ilGenerator.Emit(instruction.OpCode, (short)index);
                        break;
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                        MemberInfo memberInfo = (MemberInfo)instruction.Operand;
                        if (memberInfo.MemberType == MemberTypes.Field)
                        {
                            FieldInfo fieldInfo = (MemberInfo)instruction.Operand as FieldInfo;
                            ilGenerator.Emit(instruction.OpCode, fieldInfo);
                        }
                        else if (memberInfo.MemberType == MemberTypes.TypeInfo
                            || memberInfo.MemberType == MemberTypes.NestedType)
                        {
                            TypeInfo typeInfo = (MemberInfo)instruction.Operand as TypeInfo;
                            ilGenerator.Emit(instruction.OpCode, typeInfo);
                        }
                        else if (memberInfo.MemberType == MemberTypes.Constructor)
                        {
                            ConstructorInfo constructorInfo = memberInfo as ConstructorInfo;
                            ilGenerator.Emit(instruction.OpCode, constructorInfo);
                        }
                        else if (memberInfo.MemberType == MemberTypes.Method)
                        {
                            MethodInfo methodInfo = memberInfo as MethodInfo;
                            if (instruction.OpCode == OpCodes.Call)
                            {
                                DynamicMethod stub = GenerateStub(methodInfo);

                                ilGenerator.Emit(OpCodes.Ldtoken, methodInfo.DeclaringType);
                                ilGenerator.Emit(OpCodes.Ldc_I4, methodInfo.MetadataToken);
                                ilGenerator.Emit(OpCodes.Ldstr, StubHelper.GetMethodTypeArguments(methodInfo));

                                ilGenerator.Emit(instruction.OpCode, stub);
                            }
                            else
                            {
                                ilGenerator.Emit(instruction.OpCode, methodInfo);
                            }
                        }
                        else
                        {
                            throw new NotSupportedException();
                        }
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }

            return dynamicMethod;
        }

        public DynamicMethod GenerateStub(MethodInfo methodInfo)
        {
            ParameterInfo[] parameters = methodInfo.GetParameters();
            int count = parameters.Length + (methodInfo.IsStatic ? 0 : 1);

            List<Type> signatureParamTypes = new List<Type>();
            List<Type> parameterTypes = new List<Type>();
            if (!methodInfo.IsStatic)
                signatureParamTypes.Add(methodInfo.DeclaringType);

            signatureParamTypes.AddRange(parameters.Select(p => p.ParameterType));
            parameterTypes.AddRange(signatureParamTypes);
            parameterTypes.Add(typeof(Type));
            parameterTypes.Add(typeof(int));
            parameterTypes.Add(typeof(string));

            DynamicMethod stub = new DynamicMethod(
                string.Format("dynamic_{0}_{1}", methodInfo.DeclaringType, methodInfo.Name),
                methodInfo.ReturnType,
                parameterTypes.ToArray());

            ILGenerator ilGenerator = stub.GetILGenerator();
            ilGenerator.DeclareLocal(typeof(MethodInfo));

            for (int i = count; i < parameterTypes.Count; i++)
                ilGenerator.Emit(OpCodes.Ldarg, i);

            ilGenerator.Emit(OpCodes.Call, typeof(StubHelper).GetMethod("FindMethod"));
            ilGenerator.Emit(OpCodes.Stloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Call, typeof(MethodRewriter).GetMethod("CreateRewriter", new Type[] { typeof(MethodBase) }));
            ilGenerator.Emit(OpCodes.Callvirt, typeof(MethodRewriter).GetMethod("Rewrite"));
            ilGenerator.Emit(OpCodes.Stloc_0);
            for (int i = 0; i < count; i++)
                ilGenerator.Emit(OpCodes.Ldarg, i);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Call, typeof(StubHelper).GetMethod("GetMethodPointer"));
            ilGenerator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, methodInfo.ReturnType, signatureParamTypes.ToArray(), null);
            ilGenerator.Emit(OpCodes.Ret);
            return stub;
        }
    }
}