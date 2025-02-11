﻿using System.Collections.Generic;
using System.Linq;
using CppSharp.AST;
using CppSharp.AST.Extensions;

namespace CppSharp.Passes
{
    /// <summary>
    /// This pass generates internal classes that implement abstract classes.
    /// When the return type of a function is abstract, these internal
    /// classes are used instead since the real type cannot be resolved 
    /// while binding an allocatable class that supports proper polymorphism.
    /// </summary>
    public class GenerateAbstractImplementationsPass : TranslationUnitPass
    {
        public GenerateAbstractImplementationsPass()
            => VisitOptions.ResetFlags(VisitFlags.Default);

        /// <summary>
        /// Collects all internal implementations in a unit to be added at
        /// the end because the unit cannot be changed while it's being
        /// iterated though.
        /// </summary>
        private readonly List<Class> internalImpls = new List<Class>();

        public override bool VisitTranslationUnit(TranslationUnit unit)
        {
            var result = base.VisitTranslationUnit(unit);
            foreach (var internalImpl in internalImpls)
                if (internalImpl.Namespace != null)
                    internalImpl.Namespace.Declarations.Add(internalImpl);
                else
                    unit.Declarations.AddRange(internalImpls);

            internalImpls.Clear();
            return result;
        }

        public override bool VisitClassDecl(Class @class)
        {
            if (!base.VisitClassDecl(@class) || @class.Ignore)
                return false;

            if (@class.CompleteDeclaration != null)
                return VisitClassDecl(@class.CompleteDeclaration as Class);

            if (@class.IsAbstract && (!@class.IsTemplate || Options.GenerateClassTemplates))
            {
                foreach (var ctor in from ctor in @class.Constructors
                                     where ctor.Access == AccessSpecifier.Public
                                     select ctor)
                    ctor.Access = AccessSpecifier.Protected;
                internalImpls.Add(AddInternalImplementation(@class));
            }

            return @class.IsAbstract;
        }

        private static T AddInternalImplementation<T>(T @class) where T : Class, new()
        {
            var internalImpl = GetInternalImpl(@class);

            var abstractMethods = GetRelevantAbstractMethods(@class);
            foreach (var abstractMethod in abstractMethods)
            {
                var impl = new Method(abstractMethod)
                    {
                        Namespace = internalImpl,
                        OriginalFunction = abstractMethod,
                        IsPure = false,
                        SynthKind = abstractMethod.SynthKind == FunctionSynthKind.DefaultValueOverload ?
                            FunctionSynthKind.DefaultValueOverload : FunctionSynthKind.AbstractImplCall
                    };
                impl.OverriddenMethods.Clear();
                impl.OverriddenMethods.Add(abstractMethod);
                if (abstractMethod.OriginalReturnType.Type.IsDependentPointer() ||
                    abstractMethod.Parameters.Any(p => p.Type.IsDependentPointer()))
                {
                    // this is an extension but marks the class as an abstract impl
                    impl.ExplicitlyIgnore();
                }
                internalImpl.Methods.Add(impl);
            }

            internalImpl.Layout = @class.Layout;

            return internalImpl;
        }

        private static T GetInternalImpl<T>(T @class) where T : Class, new()
        {
            var internalImpl = new T
                                {
                                    Name = @class.Name + "Internal",
                                    Access = AccessSpecifier.Private,
                                    Namespace = @class.Namespace
                                };
            if (@class.IsDependent)
            {
                internalImpl.IsDependent = true;
                internalImpl.TemplateParameters.AddRange(@class.TemplateParameters);
                foreach (var specialization in @class.Specializations)
                {
                    var specializationImpl = AddInternalImplementation(specialization);
                    specializationImpl.Arguments.AddRange(specialization.Arguments);
                    specializationImpl.TemplatedDecl = specialization.TemplatedDecl;
                    internalImpl.Specializations.Add(specializationImpl);
                }
            }

            var @base = new BaseClassSpecifier { Type = new TagType(@class) };
            internalImpl.Bases.Add(@base);

            return internalImpl;
        }

        private static IEnumerable<Method> GetRelevantAbstractMethods(Class @class)
        {
            var abstractMethods = @class.GetAbstractMethods();
            var overriddenMethods = GetOverriddenMethods(@class);

            for (var i = abstractMethods.Count - 1; i >= 0; i--)
            {
                var @abstract = abstractMethods[i];
                var @override = overriddenMethods.Find(m => m.Name == @abstract.Name && 
                    m.ReturnType == @abstract.ReturnType && 
                    m.Parameters.SequenceEqual(@abstract.Parameters, ParameterTypeComparer.Instance));
                if (@override != null)
                {
                    if (@abstract.IsOverride)
                    {
                        var abstractMethod = abstractMethods[i];
                        bool found;
                        var rootBaseMethod = abstractMethod;
                        do
                        {
                            rootBaseMethod = rootBaseMethod.BaseMethod;
                            if (found = (rootBaseMethod == @override))
                                break;
                        } while (rootBaseMethod != null);
                        if (!found)
                            abstractMethods.RemoveAt(i);
                    }
                    else
                    {
                        abstractMethods.RemoveAt(i);
                    }
                }
            }

            return abstractMethods;
        }

        private static List<Method> GetOverriddenMethods(Class @class)
        {
            var overriddenMethods = @class.Methods.Where(m => m.IsOverride && !m.IsPure).ToList();
            foreach (var @base in @class.Bases)
                overriddenMethods.AddRange(GetOverriddenMethods(@base.Class));

            return overriddenMethods;
        }
    }
}
