﻿/*
    Copyright (C) 2011-2013 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.CodeFort {
	class PasswordInfo {
		public string passphrase;
		public string salt;
		public string iv;

		public PasswordInfo(string passphrase, string salt, string iv) {
			this.passphrase = passphrase;
			this.salt = salt;
			this.iv = iv;
		}

		public override string ToString() {
			return string.Format("P:{0}, S:{1}, I:{2}", passphrase, salt, iv);
		}
	}

	class PasswordFinder {
		byte[] serializedData;
		System.Collections.IList asmTypes;

		class Obj {
			object obj;

			public Obj(object obj) {
				this.obj = obj;
			}

			public string Name {
				get { return (string)readField("Name"); }
			}

			public List<Obj> Members {
				get { return getList("Members"); }
			}

			public List<Obj> Instructions {
				get { return getList("Instructions"); }
			}

			public object Operand {
				get { return readField("Operand"); }
			}

			public string OpCode {
				get { return (string)readField("OpCode"); }
			}

			public Obj MemberDef {
				get { return new Obj(readField("MemberDef")); }
			}

			protected object readField(string name) {
				return PasswordFinder.readField(obj, name);
			}

			public Obj findMethod(string name) {
				foreach (var member in Members) {
					if (member.obj.GetType().ToString() != "MethodDef")
						continue;
					if (member.Name != name)
						continue;

					return member;
				}

				throw new ApplicationException(string.Format("Could not find method {0}", name));
			}

			List<Obj> getList(string name) {
				return convertList((System.Collections.IList)readField(name));
			}

			static List<Obj> convertList(System.Collections.IList inList) {
				var outList = new List<Obj>(inList.Count);
				foreach (var e in inList)
					outList.Add(new Obj(e));
				return outList;
			}

			public override string ToString() {
				return Name;
			}
		}

		public PasswordFinder(byte[] serializedData) {
			this.serializedData = serializedData;
		}

		static object readField(object instance, string name) {
			return instance.GetType().GetField(name).GetValue(instance);
		}

		static System.Collections.IList toList(object obj) {
			return (System.Collections.IList)obj;
		}

		public void find(out PasswordInfo mainAsmPassword, out PasswordInfo embedPassword) {
			var asmBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("asm"), AssemblyBuilderAccess.Run);
			var moduleBuilder = asmBuilder.DefineDynamicModule("mod");
			var serializedTypes = new SerializedTypes(moduleBuilder);
			var allTypes = serializedTypes.deserialize(serializedData);
			asmTypes = toList(readField(allTypes, "Types"));

			mainAsmPassword = findMainAssemblyPassword();
			embedPassword = findEmbedPassword();
		}

		Obj findType(string name) {
			foreach (var tmp in asmTypes) {
				var type = new Obj(tmp);
				if (type.Name == name)
					return type;
			}
			return null;
		}

		PasswordInfo findMainAssemblyPassword() {
			var type = findType("BootstrapDynArguments");
			var cctor = type.findMethod(".cctor");
			var instrs = cctor.Instructions;
			var passphrase = findStringStoreValue(instrs, "KeyPassphrase");
			var salt = findStringStoreValue(instrs, "KeySaltValue");
			var iv = findStringStoreValue(instrs, "KeyIV");
			return new PasswordInfo(passphrase, salt, iv);
		}

		static string findStringStoreValue(List<Obj> instrs, string fieldName) {
			for (int i = 0; i < instrs.Count - 1; i++) {
				var ldstr = instrs[i];
				if (ldstr.OpCode != "ldstr")
					continue;
				var stsfld = instrs[i + 1];
				if (stsfld.OpCode != "stsfld")
					continue;
				var memberRef = new Obj(stsfld.Operand);
				if (memberRef.MemberDef == null)
					continue;
				if (memberRef.MemberDef.Name != fieldName)
					continue;

				return (string)ldstr.Operand;
			}

			return null;
		}

		PasswordInfo findEmbedPassword() {
			var type = findType("CilEmbeddingHelper");
			if (type == null)
				return null;
			var method = type.findMethod("CurrentDomain_AssemblyResolve");
			var instrs = method.Instructions;
			for (int i = 0; i < instrs.Count - 3; i++) {
				int index = i;

				var ldstr1 = instrs[index++];
				if (ldstr1.OpCode != "ldstr")
					continue;
				var passphrase = getString(ldstr1, instrs, ref index);

				var ldstr2 = instrs[index++];
				if (ldstr2.OpCode != "ldstr")
					continue;
				var salt = getString(ldstr2, instrs, ref index);

				var ldc = instrs[index++];
				if (!ldc.OpCode.StartsWith("ldc.i4"))
					continue;

				var ldstr3 = instrs[index++];
				if (ldstr3.OpCode != "ldstr")
					continue;
				var iv = getString(ldstr3, instrs, ref index);

				return new PasswordInfo(passphrase, salt, iv);
			}

			return null;
		}

		static string getString(Obj ldstr, List<Obj> instrs, ref int index) {
			var s = (string)ldstr.Operand;
			if (index >= instrs.Count)
				return s;
			var call = instrs[index];
			if (call.OpCode != "call" && call.OpCode != "callvirt")
				return s;
			index++;
			var op = new Obj(call.Operand);
			if (op.Name == "ToUpper")
				return s.ToUpper();
			if (op.Name == "ToLower")
				return s.ToLower();
			throw new ApplicationException(string.Format("Unknown method {0}", op.Name));
		}
	}
}
