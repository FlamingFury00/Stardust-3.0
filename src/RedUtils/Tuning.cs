using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace RedUtils
{
	/// <summary>
	/// Overrides tunable static fields by name, "Type.Field=value,...", so parameter variants of one
	/// build can be compared in matches and drills without rebuilding. Types are found by simple name
	/// in the given assemblies; only mutable static fields can be set. Needs the reflection metadata a
	/// JIT build keeps and a trimmed or native build may not.
	/// </summary>
	public static class Tuning
	{
		/// <summary>Applies the assignments and returns a "Type.Field = value" line for each.</summary>
		[RequiresUnreferencedCode("Finds types and fields by name, which trimming can remove.")]
		public static IReadOnlyList<string> Apply(string assignments, params Assembly[] assemblies)
		{
			var applied = new List<string>();
			if (string.IsNullOrWhiteSpace(assignments)) return applied;
			Type[] types = assemblies.Distinct().SelectMany(a => a.GetTypes()).ToArray();
			foreach (string assignment in assignments.Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = assignment.Split('=');
				int dot = parts[0].LastIndexOf('.');
				if (parts.Length != 2 || dot < 0)
					throw new ArgumentException($"Expected Type.Field=value, got '{assignment}'.");
				string typeName = parts[0][..dot].Trim(), member = parts[0][(dot + 1)..].Trim();
				Type type = types.SingleOrDefault(t => t.Name == typeName)
					?? throw new ArgumentException($"No single type named {typeName}.");
				FieldInfo field = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (field == null || field.IsLiteral || field.IsInitOnly)
					throw new ArgumentException($"{typeName} has no mutable static field {member}.");
				field.SetValue(null, Convert.ChangeType(parts[1].Trim(), field.FieldType, CultureInfo.InvariantCulture));
				applied.Add(string.Create(CultureInfo.InvariantCulture, $"{typeName}.{member} = {field.GetValue(null)}"));
			}
			return applied;
		}
	}
}
