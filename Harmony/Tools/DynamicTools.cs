using Harmony.ILCopying;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Harmony
{
	public static class DynamicTools
	{
		public static DynamicMethod CreateDynamicMethod(MethodBase original, string suffix)
		{
			if (original == null) throw new ArgumentNullException("original cannot be null");
			var patchName = original.Name + suffix;
			patchName = patchName.Replace("<>", "");

			var parameters = original.GetParameters();
			var result = parameters.Types().ToList();
			if (original.IsStatic == false)
				result.Insert(0, typeof(object));
			var paramTypes = result.ToArray();

			var method = new DynamicMethod(
				patchName,
				MethodAttributes.Public | MethodAttributes.Static,
				CallingConventions.Standard,
				AccessTools.GetReturnedType(original),
				paramTypes,
				original.DeclaringType,
				true
			);

			for (var i = 0; i < parameters.Length; i++)
				method.DefineParameter(i + 1, parameters[i].Attributes, parameters[i].Name);

			return method;
		}

		public static ILGenerator CreateSaveableMethod(MethodBase original, string suffix, out AssemblyBuilder assemblyBuilder, out TypeBuilder typeBuilder)
		{
			var assemblyName = new AssemblyName("DebugAssembly");
			var path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
			assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndSave, path);
			var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, assemblyName.Name + ".dll");
			typeBuilder = moduleBuilder.DefineType("Debug" + original.DeclaringType.Name, TypeAttributes.Public);

			if (original == null) throw new ArgumentNullException("original cannot be null");
			var patchName = original.Name + suffix;
			patchName = patchName.Replace("<>", "");

			var parameters = original.GetParameters();
			var result = parameters.Types().ToList();
			if (original.IsStatic == false)
				result.Insert(0, typeof(object));
			var paramTypes = result.ToArray();

			var methodBuilder = typeBuilder.DefineMethod(
				patchName,
				MethodAttributes.Public | MethodAttributes.Static,
				CallingConventions.Standard,
				AccessTools.GetReturnedType(original),
				paramTypes
			);

			return methodBuilder.GetILGenerator();
		}

		public static void SaveMethod(AssemblyBuilder assemblyBuilder, TypeBuilder typeBuilder)
		{
			var t = typeBuilder.CreateType();
			assemblyBuilder.Save("HarmonyDebugAssembly.dll");
		}

		public static LocalBuilder[] DeclareLocalVariables(MethodBase original, ILGenerator il, bool logOutput = true)
		{
			var vars = original.GetMethodBody()?.LocalVariables;
			if (vars == null)
				return new LocalBuilder[0];
			return vars.Select(lvi =>
			{
				var localBuilder = il.DeclareLocal(lvi.LocalType, lvi.IsPinned);
				if (logOutput)
					Emitter.LogLocalVariable(il, localBuilder);
				return localBuilder;
			}).ToArray();
		}

		public static LocalBuilder DeclareLocalVariable(ILGenerator il, Type type)
		{
			if (type.IsByRef) type = type.GetElementType();

			if (AccessTools.IsClass(type))
			{
				var v = il.DeclareLocal(type);
				Emitter.LogLocalVariable(il, v);
				Emitter.Emit(il, OpCodes.Ldnull);
				Emitter.Emit(il, OpCodes.Stloc, v);
				return v;
			}
			if (AccessTools.IsStruct(type))
			{
				var v = il.DeclareLocal(type);
				Emitter.LogLocalVariable(il, v);
				Emitter.Emit(il, OpCodes.Ldloca, v);
				Emitter.Emit(il, OpCodes.Initobj, type);
				return v;
			}
			if (AccessTools.IsValue(type))
			{
				var v = il.DeclareLocal(type);
				Emitter.LogLocalVariable(il, v);
				if (type == typeof(float))
					Emitter.Emit(il, OpCodes.Ldc_R4, (float)0);
				else if (type == typeof(double))
					Emitter.Emit(il, OpCodes.Ldc_R8, (double)0);
				else if (type == typeof(long))
					Emitter.Emit(il, OpCodes.Ldc_I8, (long)0);
				else
					Emitter.Emit(il, OpCodes.Ldc_I4, 0);
				Emitter.Emit(il, OpCodes.Stloc, v);
				return v;
			}
			return null;
		}

		public static void PrepareDynamicMethod(DynamicMethod method)
		{
			var nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
			var nonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

			// on mono, just call 'CreateDynMethod'
			//
			var m_CreateDynMethod = typeof(DynamicMethod).GetMethod("CreateDynMethod", nonPublicInstance);
			if (m_CreateDynMethod != null)
			{
				m_CreateDynMethod.Invoke(method, new object[0]);
				return;
			}

			// on all .NET Core versions, call 'RuntimeHelpers._CompileMethod' but with a different parameter:
			//
			var m__CompileMethod = typeof(RuntimeHelpers).GetMethod("_CompileMethod", nonPublicStatic);

			var m_GetMethodDescriptor = typeof(DynamicMethod).GetMethod("GetMethodDescriptor", nonPublicInstance);
			var handle = (RuntimeMethodHandle)m_GetMethodDescriptor.Invoke(method, new object[0]);

			// 1) RuntimeHelpers._CompileMethod(handle.GetMethodInfo())
			//
			var m_GetMethodInfo = typeof(RuntimeMethodHandle).GetMethod("GetMethodInfo", nonPublicInstance);
			if (m_GetMethodInfo != null)
			{
				var runtimeMethodInfo = m_GetMethodInfo.Invoke(handle, new object[0]);
				m__CompileMethod.Invoke(null, new object[] { runtimeMethodInfo });
				return;
			}

			// 2) RuntimeHelpers._CompileMethod(handle.Value)
			//
			if (m__CompileMethod.GetParameters()[0].ParameterType == typeof(IntPtr))
			{
				m__CompileMethod.Invoke(null, new object[] { handle.Value });
				return;
			}

			// 3) RuntimeHelpers._CompileMethod(handle)
			//
			m__CompileMethod.Invoke(null, new object[] { handle });
		}
	}
}