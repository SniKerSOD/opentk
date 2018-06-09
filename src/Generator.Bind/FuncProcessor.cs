﻿﻿//
// The Open Toolkit Library License
//
// Copyright (c) 2006 - 2013 Stefanos Apostolopoulos for the Open Toolkit library.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.XPath;
using Bind.Generators;
using Bind.Structures;

namespace Bind
{
    /// <summary>
    /// Processing class for combining the enums and delegates into a final set of functions.
    /// </summary>
    internal class FuncProcessor
    {
        private static readonly Regex Endings = new Regex(
            @"([fd]v?|u?[isb](64)?v?|v|i_v|fi)$",
            RegexOptions.Compiled);

        private static readonly Regex EndingsNotToTrim = new Regex(
            "(sh|ib|[tdrey]s|[eE]n[vd]|bled" +
            "|Attrib|Access|Boolean|Coord|Depth|Feedbacks|Finish|Flag" +
            "|Groups|IDs|Indexed|Instanced|Pixels|Queries|Status|Tess|Through" +
            "|Uniforms|Varyings|Weight|Width)$",
            RegexOptions.Compiled);

        private static readonly Regex EndingsAddV = new Regex("^0", RegexOptions.Compiled);

        private readonly IEnumerable<string> _overrides;

        /// <summary>
        /// Initializes a new instance of the <see cref="FuncProcessor"/> class.
        /// </summary>
        /// <param name="generator">The API generator.</param>
        /// <param name="overrides">The override files.</param>
        public FuncProcessor(IGenerator generator, IEnumerable<string> overrides)
        {
            Generator = generator ?? throw new ArgumentNullException(nameof(generator));
            _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        }

        private IGenerator Generator { get; }

        /// <summary>
        /// Consumes a set of enums and delegates to produce usable functions.
        /// </summary>
        /// <param name="enumProcessor">The enumeration processor.</param>
        /// <param name="docProcessor">The documentation processor.</param>
        /// <param name="delegates">The delegates.</param>
        /// <param name="enums">The enums.</param>
        /// <param name="apiname">The name of the API to produce a function collection for.</param>
        /// <param name="apiversion">The version of the API to produce a function collection for.</param>
        /// <returns>A collection of functions.</returns>
        public FunctionCollection Process
        (
            EnumProcessor enumProcessor,
            DocProcessor docProcessor,
            DelegateCollection delegates,
            EnumCollection enums,
            string apiname,
            string apiversion
        )
        {
            foreach (var file in _overrides)
            {
                Console.WriteLine("Processing funcs in {0}.", file);

                var nav = new XPathDocument(file).CreateNavigator();
                foreach (var version in apiversion.Split('|'))
                {
                    // Translate each delegate:
                    // 1st using the <replace> elements in overrides.xml
                    // 2nd using the hardcoded rules in FuncProcessor (e.g. char* -> string)
                    foreach (var signatures in delegates.Values)
                    {
                        foreach (var d in signatures)
                        {
                            var replace = GetFuncOverride(nav, d, apiname, apiversion);
                            TranslateExtension(d);
                            TranslateReturnType(d, replace, nav, enumProcessor, enums, apiname);
                            TranslateParameters(d, replace, nav, enumProcessor, enums, apiname);
                            TranslateAttributes(d, replace);
                        }
                    }

                    // Create overloads for backwards compatibility,
                    // by resolving <overload> elements
                    var overloadList = new List<DelegateDefinition>();
                    foreach (var d in delegates.Values.Select(v => v.First()))
                    {
                        var overloadElements = GetFuncOverload(nav, d, apiname, apiversion);
                        foreach (XPathNavigator overloadElement in overloadElements)
                        {
                            var overload = new DelegateDefinition(d);
                            TranslateReturnType(overload, overloadElement, nav, enumProcessor, enums, apiname);
                            TranslateParameters(overload, overloadElement, nav, enumProcessor, enums, apiname);
                            TranslateAttributes(overload, overloadElement);
                            overloadList.Add(overload);
                        }
                    }

                    foreach (var overload in overloadList)
                    {
                        delegates.Add(overload);
                    }
                }
            }

            Console.WriteLine("Generating wrappers.");
            var wrappers = CreateWrappers(delegates);

            Console.WriteLine("Generating convenience overloads.");
            wrappers.AddRange(CreateConvenienceOverloads(wrappers));

            Console.WriteLine("Removing overloaded delegates.");
            RemoveOverloadedDelegates(delegates, wrappers);

            Console.WriteLine("Generating address table.");
            GenerateAddressTable(delegates);

            Console.WriteLine("Generating documentation.");
            GenerateDocumentation(wrappers, enumProcessor, docProcessor);

            return wrappers;
        }

        private void GenerateDocumentation(FunctionCollection wrappers, EnumProcessor enumProcessor, DocProcessor docProcessor)
        {
            foreach (var list in wrappers)
            {
                foreach (var f in list.Value)
                {
                    f.DocumentationDefinition = docProcessor.Process(f, enumProcessor);
                }
            }
        }

        private void GenerateAddressTable(DelegateCollection delegates)
        {
            // We allocate one slot per entry point. Rules:
            // - All extensions get a slot
            // - Core functions get a slot, unless UseDllImports is enabled
            // - On Windows, core functions with version > 1.1 must be treated as extensions.
            //   This is controlled via the UseWindowsCompatibleGL setting.
            // Entry points without a slot are assigned the magic slot index -1.
            // Generator.Rewrite detects this and generates a static DllImport call
            // instead of a calli instruction for these functions.

            var slot = -1;
            foreach (var list in delegates.Values)
            {
                slot++;
                foreach (var d in list)
                {
                    d.Slot = slot;
                }
            }
        }

        // When we have a list of overloaded delegates, make sure that
        // all generated wrappers use the first (original) delegate, not
        // the overloaded ones. This allows us to reduce the amount
        // of delegates we need to generate (1 per entry point instead
        // of 1 per overload), which improves loading times.
        private static void RemoveOverloadedDelegates(DelegateCollection delegates, FunctionCollection wrappers)
        {
            foreach (var w in wrappers.Values.SelectMany(w => w))
            {
                var d = delegates[w.Name].First();
                w.WrappedDelegateDefinition = d;
            }
        }

        private static string GetPath(string apipath, string apiname, string apiversion, string function, string extension)
        {
            var path = new StringBuilder();
            path.Append("/signatures/");
            path.Append(apipath);
            if (!string.IsNullOrEmpty(apiname) && !string.IsNullOrEmpty(apiversion))
            {
                path.Append($"[contains(concat('|', @name, '|'), '|{apiname}|') and " +
                            $"(contains(concat('|', @version, '|'), '|{apiversion}|') or not(boolean(@version)))]");
            }
            else if (!string.IsNullOrEmpty(apiname))
            {
                path.Append($"[contains(concat('|', @name, '|'), '|{apiname}|')]");
            }
            else if (!string.IsNullOrEmpty(apiversion))
            {
                path.Append($"[contains(concat('|', @version, '|'), '|{apiversion}|') or not(boolean(@version))]");
            }

            if (function == null)
            {
                return path.ToString();
            }

            if (extension != null)
            {
                // match an override that has this specific extension
                // *or* one that has no extension at all (equivalent
                // to "match all possible extensions")
                path.Append($"/function[contains(concat('|', @name, '|'), '|{function}|') and " +
                            $"(contains(concat('|', @extension, '|'), '|{extension}|') or not(boolean(@extension)))]");
            }
            else
            {
                path.Append($"/function[contains(concat('|', @name, '|'), '|{function}|')]");
            }

            return path.ToString();
        }

        private static string GetOverloadsPath(string apiname, string apiversion, string function, string extension)
        {
            return GetPath("overload", apiname, apiversion, function, extension);
        }

        private static string GetOverridesPath(string apiname, string apiversion, string function, string extension)
        {
            return GetPath("replace", apiname, apiversion, function, extension);
        }

        private void TranslateType(TypeDefinition typeDefinition, XPathNavigator overrides, EnumProcessor enumProcessor, EnumCollection enums, string category, string apiname)
        {
            category = enumProcessor.TranslateEnumName(category);

            // Try to find out if it is an enum. If the type exists in the normal GLEnums list, use this.
            // Special case for Boolean which is there simply because C89 does not support bool types.
            // We don't really need that in C#
            var normal =
                enums.TryGetValue(typeDefinition.CurrentType, out var @enum) ||
                enums.TryGetValue(enumProcessor.TranslateEnumName(typeDefinition.CurrentType), out @enum);

            // Translate enum types
            typeDefinition.IsEnum = false;
            if (normal && @enum.Name != "GLenum" && @enum.Name != "Boolean")
            {
                typeDefinition.IsEnum = true;

                // Some functions and enums have the same names.
                // Make sure we reference the enums rather than the functions.
                typeDefinition.QualifiedType = $"{Generator.Namespace}.{@enum.Name}";
            }
            else if (Generator.APITypes.TryGetValue(typeDefinition.CurrentType, out var s))
            {
                // Check if the parameter is a generic GLenum. If it is, search for a better match,
                // otherwise fallback to Settings.CompleteEnumName (named 'All' by default).
                if (s.Contains("GLenum") /*&& !String.IsNullOrEmpty(category)*/)
                {
                    typeDefinition.IsEnum = true;

                    // Better match: enum.Name == function.Category (e.g. GL_VERSION_1_1 etc)
                    if (enums.ContainsKey(category))
                    {
                        typeDefinition.QualifiedType = enumProcessor.TranslateEnumName(category);
                    }
                    else
                    {
                        // Should have used the "All" enum, which is now gone.
                        Trace.WriteLine
                        (
                            $"[Warning] Could not determine actual enum type for parameter {typeDefinition}. Using weakly typed" +
                            $" integer instead - please specify an override in overrides.xml."
                        );

                        typeDefinition.IsEnum = false;
                        typeDefinition.QualifiedType = "int";
                    }
                }
                else
                {
                    typeDefinition.QualifiedType = s;
                }
            }

            if (!typeDefinition.IsArray && !typeDefinition.IsPointer && !typeDefinition.IsReference &&
                (typeDefinition.QualifiedType.ToLower().Contains("buffersize") ||
                 typeDefinition.QualifiedType.ToLower().Contains("sizeiptr") ||
                 typeDefinition.QualifiedType.Contains("size_t")))
            {
                typeDefinition.WrapperType |= WrapperTypes.SizeParameter;
            }

            typeDefinition.CurrentType =
                Generator.LanguageTypes.ContainsKey(typeDefinition.CurrentType)
                    ? Generator.LanguageTypes[typeDefinition.CurrentType]
                    : typeDefinition.CurrentType;

            // Make sure that enum parameters follow enum overrides, i.e.
            // if enum ErrorCodes is overriden to ErrorCode, then parameters
            // of type ErrorCodes should also be overriden to ErrorCode.
            var enumOverride = overrides.SelectSingleNode(
                EnumProcessor.GetOverridesNodePath(apiname, typeDefinition.CurrentType));
            if (enumOverride != null)
            {
                // For consistency - many overrides use string instead of String.
                if (enumOverride.Value == "string")
                {
                    typeDefinition.QualifiedType = "String";
                }
                else if (enumOverride.Value == "StringBuilder")
                {
                    throw new NotSupportedException("StringBuilder enum overrides are no longer supported");
                }
                else
                {
                    typeDefinition.CurrentType = enumOverride.Value;
                }
            }

            if (typeDefinition.CurrentType == "IntPtr" && string.IsNullOrEmpty(typeDefinition.PreviousType))
            {
                typeDefinition.IndirectionLevel = 0;
            }

            if (typeDefinition.IndirectionLevel >= 3)
            {
                Trace.WriteLine(
                    $"[Error] Type '{typeDefinition}' has a high pointer level. Bindings will be incorrect.");
            }

            if (!typeDefinition.IsEnum)
            {
                // Remove qualifier if type is not an enum
                // Resolves issues when replacing / overriding
                // an enum parameter with a non-enum type
                typeDefinition.QualifiedType = typeDefinition.CurrentType;
            }
        }

        private static string TranslateExtension(string extension)
        {
            extension = extension.ToUpper();
            if (extension.Length > 2)
            {
                extension = extension[0] + extension.Substring(1).ToLower();
            }

            return extension;
        }

        private void TranslateExtension(DelegateDefinition d)
        {
            d.Extension = TranslateExtension(d.Extension);
        }

        private static string GetTrimmedExtension(string name, string extension)
        {
            // Extensions are always uppercase
            var index = name.LastIndexOf(extension.ToUpper(), StringComparison.Ordinal);
            if (index >= 0)
            {
                name = name.Remove(index);
            }

            return name;
        }

        // Trims unecessary suffices from the specified OpenGL function name.
        private static string GetTrimmedName(DelegateDefinition d)
        {
            var name = d.Name;
            var extension = d.Extension;
            var trimmedName = GetTrimmedExtension(name, extension);

            // Note: some endings should not be trimmed, for example: 'b' from Attrib.
            // Check the endingsNotToTrim regex for details.
            var m = EndingsNotToTrim.Match(trimmedName);
            if (m.Index + m.Length != trimmedName.Length)
            {
                m = Endings.Match(trimmedName);

                if (m.Length > 0 && m.Index + m.Length == trimmedName.Length)
                {
                    // Only trim endings, not internal matches.
                    if (m.Value[m.Length - 1] == 'v' && EndingsAddV.IsMatch(name) &&
                        !name.StartsWith("Get") && !name.StartsWith("MatrixIndex"))
                    {
                        // Only trim ending 'v' when there is a number
                        trimmedName = trimmedName.Substring(0, m.Index) + "v";
                    }
                    else
                    {
                        if (!trimmedName.EndsWith("xedv"))
                        {
                            trimmedName = trimmedName.Substring(0, m.Index);
                        }
                        else
                        {
                            trimmedName = trimmedName.Substring(0, m.Index + 1);
                        }
                    }
                }
            }

            return trimmedName;
        }

        private static XPathNodeIterator GetFuncOverload(XPathNavigator nav, DelegateDefinition d, string apiname, string apiversion)
        {
            // Try a few different extension variations that appear in the overrides xml file
            string[] extensions = { d.Extension, TranslateExtension(d.Extension), d.Extension.ToUpper() };
            var trimmedName = GetTrimmedName(d);
            XPathNodeIterator functionOverload = null;

            foreach (var ext in extensions)
            {
                var extensionlessName = GetTrimmedExtension(d.Name, ext);
                functionOverload = nav.Select(GetOverloadsPath(apiname, apiversion, d.Name, ext));
                if (functionOverload.Count != 0)
                {
                    break;
                }

                functionOverload = nav.Select(GetOverloadsPath(apiname, apiversion, extensionlessName, ext));
                if (functionOverload.Count != 0)
                {
                    break;
                }

                functionOverload = nav.Select(GetOverloadsPath(apiname, apiversion, trimmedName, ext));
                if (functionOverload.Count != 0)
                {
                    break;
                }
            }

            return functionOverload;
        }

        private static XPathNavigator GetFuncOverride(XPathNavigator nav, DelegateDefinition d, string apiname, string apiversion)
        {
            // Try a few different extension variations that appear in the overrides xml file
            string[] extensions = { d.Extension, TranslateExtension(d.Extension), d.Extension.ToUpper() };
            var trimmedName = GetTrimmedName(d);
            XPathNavigator functionOverride = null;

            foreach (var ext in extensions)
            {
                var extensionlessName = GetTrimmedExtension(d.Name, ext);
                functionOverride =
                    nav.SelectSingleNode(GetOverridesPath(apiname, apiversion, d.Name, ext)) ??
                    nav.SelectSingleNode(GetOverridesPath(apiname, apiversion, extensionlessName, ext)) ??
                    nav.SelectSingleNode(GetOverridesPath(apiname, apiversion, trimmedName, ext));

                if (functionOverride != null)
                {
                    break;
                }
            }

            return functionOverride;
        }

        private void TrimName(FunctionDefinition f)
        {
            f.TrimmedName = GetTrimmedName(f);
        }

        private static void ApplyParameterReplacement(DelegateDefinition d, XPathNavigator functionOverride)
        {
            if (functionOverride == null)
            {
                return;
            }

            for (var i = 0; i < d.Parameters.Count; i++)
            {
                var paramOverride = functionOverride.SelectSingleNode(
                    $"param[@name='{d.Parameters[i].RawName}' or @index='{i}']");
                if (paramOverride == null)
                {
                    continue;
                }

                foreach (XPathNavigator node in paramOverride.SelectChildren(XPathNodeType.Element))
                {
                    switch (node.Name)
                    {
                        case "type":
                            d.Parameters[i].CurrentType = (string)node.TypedValue;
                            break;
                        case "name":
                            d.Parameters[i].Name = (string)node.TypedValue;
                            break;
                        case "flow":
                            d.Parameters[i].Flow = ParameterDefinition.GetFlowDirection((string)node.TypedValue);
                            break;
                        case "count":
                            d.Parameters[i].ComputeSize = node.Value.Trim();
                            d.Parameters[i].ElementCount =
                                uint.TryParse(d.Parameters[i].ComputeSize, out var count) ? count : 0;
                            break;
                    }
                }
            }
        }

        private static void ApplyReturnTypeReplacement(DelegateDefinition d, XPathNavigator functionOverride)
        {
            var returnOverride = functionOverride?.SelectSingleNode("returns");
            if (returnOverride != null)
            {
                d.ReturnTypeDefinition.CurrentType = returnOverride.Value;
            }
        }

        // Translates the opengl return type to the equivalent C# type.
        //
        // First, we use the official typemap (gl.tm) to get the correct type.
        // Then we override this, when it is:
        // 1) A string (we have to use Marshal.PtrToStringAnsi, to avoid heap corruption)
        // 2) An array (translates to IntPtr)
        // 3) A generic object or void* (translates to IntPtr)
        // 4) A GLenum (translates to int on Legacy.Tao or GL.Enums.GLenum otherwise).
        // Return types must always be CLS-compliant, because .Net does not support overloading on return types.
        private void TranslateReturnType
        (
            DelegateDefinition d,
            XPathNavigator functionOverride,
            XPathNavigator nav,
            EnumProcessor enumProcessor,
            EnumCollection enums,
            string apiname
        )
        {
            ApplyReturnTypeReplacement(d, functionOverride);

            TranslateType(d.ReturnTypeDefinition, nav, enumProcessor, enums, d.Category, apiname);

            if (d.ReturnTypeDefinition.CurrentType.ToLower() == "void" && d.ReturnTypeDefinition.IsPointer)
            {
                d.ReturnTypeDefinition.QualifiedType = "IntPtr";
                d.ReturnTypeDefinition.IndirectionLevel--;
                d.ReturnTypeDefinition.WrapperType |= WrapperTypes.GenericReturnType;
            }

            if (d.ReturnTypeDefinition.CurrentType.ToLower() == "string")
            {
                d.ReturnTypeDefinition.QualifiedType = "IntPtr";
                d.ReturnTypeDefinition.WrapperType |= WrapperTypes.StringReturnType;
            }

            if (d.ReturnTypeDefinition.CurrentType.ToLower() == "object")
            {
                d.ReturnTypeDefinition.QualifiedType = "IntPtr";
                d.ReturnTypeDefinition.WrapperType |= WrapperTypes.GenericReturnType;
            }

            if (d.ReturnTypeDefinition.CurrentType.Contains("GLenum"))
            {
                d.ReturnTypeDefinition.QualifiedType = "int";
            }

            if (d.ReturnTypeDefinition.CurrentType.ToLower().Contains("bool"))
            {
                d.ReturnTypeDefinition.QualifiedType = "byte";
                d.ReturnTypeDefinition.WrapperType |= WrapperTypes.BoolParameter;
            }
        }

        private void TranslateParameters
        (
            DelegateDefinition d,
            XPathNavigator functionOverride,
            XPathNavigator nav,
            EnumProcessor enumProcessor,
            EnumCollection enums,
            string apiname
        )
        {
            ApplyParameterReplacement(d, functionOverride);

            for (var i = 0; i < d.Parameters.Count; i++)
            {
                TranslateParameter(d.Parameters[i], nav, enumProcessor, enums, d.Category, apiname);
                if (d.Parameters[i].CurrentType == "UInt16" && d.Name.Contains("LineStipple"))
                {
                    d.Parameters[i].WrapperType |= WrapperTypes.UncheckedParameter;
                }

                var paramOverride = functionOverride?.SelectSingleNode(
                    $"param[@name='{d.Parameters[i].RawName}' or @index='{i}']");
                if (paramOverride == null)
                {
                    continue;
                }

                var legacyArrayParameter = paramOverride.GetAttribute("legacyArrayParameter", string.Empty);
                if (!string.IsNullOrEmpty(legacyArrayParameter))
                {
                    d.Parameters[i].WrapperType |= WrapperTypes.LegacyArrayParameter;
                }
            }
        }

        private void TranslateParameter
        (
            ParameterDefinition p,
            XPathNavigator overrides,
            EnumProcessor enumProcessor,
            EnumCollection enums,
            string category,
            string apiname
        )
        {
            TranslateType(p, overrides, enumProcessor, enums, category, apiname);

            // Translate char* -> string. This simplifies the rest of the logic below
            if (p.CurrentType.ToLower().Contains("char") && p.IsPointer)
            {
                p.CurrentType = "string";
                p.IndirectionLevel--;
            }

            // Find out the necessary wrapper types.
            if (p.CurrentType.ToLower() == "string" && !p.IsPointer)
            {
                // char* -> IntPtr
                // Due to a bug in the Mono runtime, we need
                // to marshal [out] string parameters ourselves.
                // StringBuilder crashes at runtime.
                // For symmetry, and to avoid potential runtime bugs,
                // we will also marshal [in] string types manually.
                p.QualifiedType = "IntPtr";
                p.WrapperType |= WrapperTypes.StringParameter;
            }

            if (p.CurrentType.ToLower() == "string" && p.IsPointer)
            {
                // string* -> [In] String[]
                // [Out] StringBuilder[] parameter is not currently supported
                // Higher indirection levels are also not supported
                if (p.Flow == FlowDirection.Out)
                {
                    throw new NotSupportedException("[Out] String* parameters are not currently supported.");
                }

                if (p.IndirectionLevel >= 2)
                {
                    throw new NotSupportedException("String arrays with arity >= 2 are not currently supported.");
                }

                p.QualifiedType = "IntPtr";
                p.IndirectionLevel = 0;
                p.ArrayDimensions = 0;
                p.WrapperType |= WrapperTypes.StringArrayParameter;
            }

            if (p.IsPointer && p.WrapperType == WrapperTypes.None)
            {
                if (p.QualifiedType.ToLower().StartsWith("void"))
                {
                    p.QualifiedType = "IntPtr";
                    p.IndirectionLevel = 0; // Generic parameters cannot have pointers
                    p.WrapperType |= WrapperTypes.GenericParameter;
                    p.WrapperType |= WrapperTypes.ArrayParameter;
                    p.WrapperType |= WrapperTypes.ReferenceParameter;
                }
                else
                {
                    p.WrapperType |= WrapperTypes.ArrayParameter;
                    p.WrapperType |= WrapperTypes.ReferenceParameter;
                    p.WrapperType |= WrapperTypes.PointerParameter;
                }
            }

            if (Utilities.CSharpKeywords.Contains(p.Name))
            {
                p.Name = $"@{p.Name}";
            }
        }

        private void TranslateAttributes(DelegateDefinition d, XPathNavigator functionOverride)
        {
            if (functionOverride == null)
            {
                return;
            }

            var versionOverride = functionOverride.SelectSingleNode("version");
            if (versionOverride != null)
            {
                d.Version = versionOverride.Value;
            }

            var profileOverride = functionOverride.SelectSingleNode("profile");
            if (profileOverride != null)
            {
                Debug.Print("Profile override not yet implemented");
            }

            var nameOverride = functionOverride.SelectSingleNode("name");
            if (nameOverride != null)
            {
                d.Name = nameOverride.Value;
            }

            var obsolete = functionOverride.GetAttribute("obsolete", string.Empty);
            if (!string.IsNullOrEmpty(obsolete))
            {
                d.Obsolete = obsolete;
            }
        }

        private FunctionCollection CreateWrappers(DelegateCollection delegates)
        {
            var wrappers = new FunctionCollection();
            foreach (var d in delegates.Values.SelectMany(v => v))
            {
                wrappers.AddRange(CreateNormalWrappers(d));
            }

            return wrappers;
        }

        private IEnumerable<FunctionDefinition> CreateNormalWrappers(DelegateDefinition d)
        {
            var f = new FunctionDefinition(d);
            TrimName(f);

            WrapReturnType(f);
            foreach (var wrapper in WrapParameters(f))
            {
                yield return wrapper;
            }
        }

        private IEnumerable<FunctionDefinition> CreateConvenienceOverloads(FunctionCollection wrappers)
        {
            var convenienceWrappers = new List<FunctionDefinition>();
            foreach (var d in wrappers.Values.SelectMany(w => w))
            {
                if (d.Parameters.Count > 0 && d.Parameters.Count <= 2)
                {
                    var p = d.Parameters.Last();
                    var r = d.ReturnTypeDefinition;

                    var name = d.Name;

                    var isCandidate = true;
                    isCandidate &=
                        name.StartsWith("Get") || name.StartsWith("Gen") ||
                        name.StartsWith("Delete") || name.StartsWith("New");
                    isCandidate &= p.IsPointer;
                    // if there is a specific count set, such as "4", then this function
                    // returns a vector of specific dimensions and it would be wrong
                    // to generate an overload that returns a value of different size.
                    isCandidate &= p.ElementCount == 0 || p.ElementCount == 1;
                    isCandidate &= r.CurrentType == "void" && !r.IsPointer;

                    FunctionDefinition f = null;
                    if (isCandidate && p.Flow == FlowDirection.Out)
                    {
                        // Match Gen*|Get*|New*([Out] int[] names) methods
                        f = CreateReturnTypeConvenienceWrapper(d);
                    }
                    else if (isCandidate && p.Flow != FlowDirection.Out)
                    {
                        // Match *Delete(int count, int[] names) methods
                        if (d.Parameters.Count == 2)
                        {
                            f = CreateArrayReturnTypeConvenienceWrapper(d);
                        }
                    }

                    if (f != null)
                    {
                        // If we have a convenience overload, we should turn its name from plural into singular
                        f.TrimmedName = f.TrimmedName.Replace("Queries", "Query").TrimEnd('s');

                        convenienceWrappers.Add(f);
                    }
                }

                // Check for IntPtr parameters that correspond to size_t (e.g. GLsizei)
                // and add Int32 overloads for convenience.
                {
                    FunctionDefinition f = null;
                    var i = 0;
                    foreach (var p in d.Parameters)
                    {
                        if ((p.WrapperType & WrapperTypes.SizeParameter) != 0)
                        {
                            f = f ?? new FunctionDefinition(d);
                            f.Parameters[i].QualifiedType = "Int32";
                        }

                        i++;
                    }

                    if (f != null)
                    {
                        convenienceWrappers.Add(f);
                    }
                }
            }

            return convenienceWrappers;
        }

        private static FunctionDefinition CreateReturnTypeConvenienceWrapper(FunctionDefinition d)
        {
            var f = new FunctionDefinition(d);
            f.ReturnTypeDefinition = new TypeDefinition(f.Parameters.Last());
            f.ReturnTypeDefinition.IndirectionLevel = 0;
            f.Parameters.RemoveAt(f.Parameters.Count - 1);
            f.ReturnTypeDefinition.WrapperType |= WrapperTypes.ConvenienceReturnType;

            if (f.Parameters.Count <= 0)
            {
                return f;
            }

            var pSize = f.Parameters.Last();
            if (!pSize.CurrentType.ToLower().StartsWith("int") || pSize.IsPointer)
            {
                return f;
            }

            f.Parameters.RemoveAt(f.Parameters.Count - 1);
            f.ReturnTypeDefinition.WrapperType |= WrapperTypes.ConvenienceArrayReturnType;
            return f;
        }

        private static FunctionDefinition CreateArrayReturnTypeConvenienceWrapper(FunctionDefinition d)
        {
            var f = new FunctionDefinition(d);
            var pArray = f.Parameters.Last();

            f.Parameters.RemoveAt(f.Parameters.Count - 2);
            pArray.WrapperType |= WrapperTypes.ConvenienceArrayType;
            // Since this is a 1-element overload, we don't need
            // array or reference wrappers.
            pArray.WrapperType &= ~(
                WrapperTypes.ReferenceParameter |
                WrapperTypes.ArrayParameter);
            pArray.ArrayDimensions = pArray.IndirectionLevel = 0;
            pArray.IsReference = false;
            return f;
        }

        private List<FunctionDefinition> GetWrapper(IDictionary<WrapperTypes, List<FunctionDefinition>> dictionary, WrapperTypes key, FunctionDefinition raw)
        {
            if (!dictionary.ContainsKey(key))
            {
                dictionary.Add(key, new List<FunctionDefinition>());
                if (raw != null)
                {
                    dictionary[key].Add(new FunctionDefinition(raw));
                }
            }

            return dictionary[key];
        }

        private IEnumerable<FunctionDefinition> WrapParameters(FunctionDefinition func)
        {
            if (func.Parameters.Count == 0)
            {
                // Functions without parameters do not need
                // parameter wrappers
                yield return func;
                yield break;
            }

            var wrappers = new Dictionary<WrapperTypes, List<FunctionDefinition>>();
            for (var i = 0; i < func.Parameters.Count; i++)
            {
                var parameter = func.Parameters[i];

                // Handle all non-generic parameters first.
                // Generics are handled in a second pass.
                if ((parameter.WrapperType & WrapperTypes.GenericParameter) == 0)
                {
                    if ((parameter.WrapperType & WrapperTypes.LegacyArrayParameter) != 0)
                    {
                        foreach (var wrapper in GetWrapper(wrappers, WrapperTypes.LegacyArrayParameter, func))
                        {
                            wrapper.Obsolete = "Use out overload instead";
                            var p = wrapper.Parameters[i];
                            p.ArrayDimensions++;
                            p.IndirectionLevel--;
                        }
                    }

                    if ((parameter.WrapperType & WrapperTypes.ArrayParameter) != 0)
                    {
                        foreach (var wrapper in GetWrapper(wrappers, WrapperTypes.ArrayParameter, func))
                        {
                            var p = wrapper.Parameters[i];

                            if (p.ElementCount == 1)
                            {
                                p.IsReference = true;
                            }
                            else
                            {
                                p.ArrayDimensions++;
                            }

                            p.IndirectionLevel--;
                        }
                    }

                    if ((parameter.WrapperType & WrapperTypes.ReferenceParameter) != 0)
                    {
                        foreach (var wrapper in GetWrapper(wrappers, WrapperTypes.ReferenceParameter, func))
                        {
                            var p = wrapper.Parameters[i];

                            p.IsReference = true;
                            p.IndirectionLevel--;
                        }
                    }

                    if (parameter.WrapperType == 0 ||
                        (parameter.WrapperType & WrapperTypes.ConvenienceArrayType) != 0 ||
                        (parameter.WrapperType & WrapperTypes.ConvenienceReturnType) != 0 ||
                        (parameter.WrapperType & WrapperTypes.ConvenienceArrayReturnType) != 0)
                    {
                        // We don't need to do anything, just add this function directly
                        // to the list of wrappers.
                        GetWrapper(wrappers, parameter.WrapperType, func);
                    }
                }
            }

            // Handle generic parameters
            if (wrappers.Count == 0)
            {
                // Some functions, such as VDPAUInit, only have generic parameters
                // This means no wrapper has been generated by any of the previous
                // transformations. Since the generic translation below operates on
                // existing wrappers, add one here to get the process started.
                wrappers.Add(WrapperTypes.None, new List<FunctionDefinition> { new FunctionDefinition(func) });
            }

            var list = new List<FunctionDefinition>();
            foreach (var wrapper in wrappers.Values.SelectMany(v => v))
            {
                // Add generic 'ref T' wrapper
                FunctionDefinition genericWrapper = null;
                for (var i = 0; i < wrapper.Parameters.Count; i++)
                {
                    var parameter = wrapper.Parameters[i];
                    if ((parameter.WrapperType & WrapperTypes.GenericParameter) != 0)
                    {
                        genericWrapper = genericWrapper ?? new FunctionDefinition(wrapper);
                        var p = genericWrapper.Parameters[i];

                        p.IsReference = true;
                        p.IndirectionLevel = 0;
                        p.ArrayDimensions = 0;
                        p.Generic = true;
                        p.QualifiedType = "T" + i;
                        p.Flow = FlowDirection.Undefined;
                    }
                }

                if (genericWrapper != null)
                {
                    list.Add(genericWrapper);
                }

                // Add the following wrappers:
                // 'IntPtr', 'T[]', 'T[,]' and 'T[,,]'
                for (var arity = 0; arity < 4; arity++)
                {
                    genericWrapper = null;
                    for (var i = 0; i < wrapper.Parameters.Count; i++)
                    {
                        var parameter = wrapper.Parameters[i];
                        if ((parameter.WrapperType & WrapperTypes.GenericParameter) != 0)
                        {
                            genericWrapper = genericWrapper ?? new FunctionDefinition(wrapper);

                            var p = genericWrapper.Parameters[i];

                            p.IsReference = false;
                            p.IndirectionLevel = 0;
                            p.ArrayDimensions = (uint)arity;
                            if (arity == 0)
                            {
                                p.QualifiedType = "IntPtr";
                            }
                            else
                            {
                                p.Generic = true;
                                p.QualifiedType = "T" + i;
                                p.Flow = FlowDirection.Undefined;
                            }
                        }
                    }

                    if (genericWrapper != null)
                    {
                        list.Add(genericWrapper);
                    }
                }
            }

            GetWrapper(wrappers, WrapperTypes.GenericParameter, null)
                .AddRange(list);

            // Handle string parameters
            foreach (var wrapper in wrappers.Values.SelectMany(v => v))
            {
                for (var i = 0; i < wrapper.Parameters.Count; i++)
                {
                    var p = wrapper.Parameters[i];
                    if ((p.WrapperType & WrapperTypes.StringParameter) != 0)
                    {
                        p.QualifiedType = "String";
                        if (p.Flow == FlowDirection.Out)
                        {
                            p.IsReference = true;
                        }
                    }

                    if ((p.WrapperType & WrapperTypes.StringArrayParameter) != 0)
                    {
                        if (p.Flow == FlowDirection.Out)
                        {
                            throw new NotImplementedException();
                        }

                        p.QualifiedType = "String";
                        p.IndirectionLevel = 0;
                        p.ArrayDimensions = 1;
                    }
                }
            }

            // Return all generated wrappers
            foreach (var w in wrappers.Values.SelectMany(v => v).OrderBy(v => v))
            {
                yield return w;
            }
        }

        private static void WrapReturnType(FunctionDefinition func)
        {
            if ((func.ReturnTypeDefinition.WrapperType & WrapperTypes.StringReturnType) != 0)
            {
                func.ReturnTypeDefinition.QualifiedType = "String";
            }

            if ((func.ReturnTypeDefinition.WrapperType & WrapperTypes.GenericReturnType) != 0)
            {
                // Nothing else we can do, using generics will break the runtime
                func.ReturnTypeDefinition.QualifiedType = "IntPtr";
            }

            if ((func.ReturnTypeDefinition.WrapperType & WrapperTypes.BoolParameter) != 0)
            {
                func.ReturnTypeDefinition.QualifiedType = "bool";
            }
        }
    }
}
