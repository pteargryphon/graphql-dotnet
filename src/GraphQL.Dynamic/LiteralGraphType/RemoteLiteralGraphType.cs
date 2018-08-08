using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using GraphQL.Dynamic.Types.Introspection;
using GraphQL.Introspection;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using System.IO;

namespace GraphQL.Dynamic.Types.LiteralGraphType
{
    public class RemoteLiteralGraphType : ObjectGraphType
    {
        public delegate Introspection.Schema RemoteSchemaFetcher(string url);

        private static object FetchRemoteServerSchemaMutex = new object();
        private static ConcurrentDictionary<string, Introspection.Schema> RemoteServerSchemas { get; set; } = new ConcurrentDictionary<string, Introspection.Schema>();

        private static ConcurrentDictionary<string, HashSet<Type>> RemoteServerTypes { get; set; } = new ConcurrentDictionary<string, HashSet<Type>>();

        private bool _hasAddedFields;

        private readonly string _remoteLocation;
        private readonly string _name;

        public RemoteLiteralGraphType(string remoteLocation, string name)
        {
            _remoteLocation = remoteLocation;
            _name = name;
        }

        public override string CollectTypes(TypeCollectionContext context)
        {
            if (!string.IsNullOrWhiteSpace(_remoteLocation) && !string.IsNullOrWhiteSpace(_name))
            {
                // get remote server information
                // if we haven't fetched the remote types for this remote:
                if (!RemoteServerSchemas.TryGetValue(_remoteLocation, out var schema))
                {
                    throw new Exception($"Schema not already loaded for remote {_remoteLocation}");
                }

                // get type
                if (!TryGetFieldTypeFromRemoteSchema(schema, _name, out var type))
                {
                    // if no type found: fail
                    // TODO: fail better
                    throw new Exception($"Failed to find type '{_name}' in remote '{_remoteLocation}' schema");
                }

                Name = $"{_remoteLocation}.{_name}";

                if (!_hasAddedFields)
                {
                    var fields = GetFieldsForFieldType(_remoteLocation, type).Where(f => f != null).ToList();
                    foreach (var field in fields)
                    {
                        AddField(field);
                    }

                    _hasAddedFields = true;
                }
            }

            return base.CollectTypes(context);
        }

        public static async Task<IEnumerable<Type>> LoadRemotes(IEnumerable<RemoteDescriptor> remotes, Func<TypeElement, bool> typeFilter = null, RemoteSchemaFetcher remoteSchemaFetcher = null)
        {
            if (typeFilter == null)
            {
                typeFilter = t => !t.Name.StartsWith("__") && t.Kind != TypeElementTypeKind.Scalar;
            }

            if (remoteSchemaFetcher == null)
            {
                remoteSchemaFetcher = FetchRemoteSchemaViaHttp;
            }

            var parentConstructor = typeof(RemoteLiteralGraphType).GetConstructor(new[] { typeof(string), typeof(string) });
            var metadataAttributeConstructor = typeof(RemoteLiteralGraphTypeMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string), typeof(string) });

            // Convert each remote into a new assembly asynchronously
            var tasks = remotes
                .Select(remote =>
                {
                    return Task.Run(() =>
                    {
                        var url = remote.Url;
                        var schema = FetchRemoteServerSchema(url, remoteSchemaFetcher);                      
                        
                        var types = schema.Types
                            .Where(typeFilter)
                            .Select(schemaType =>
                            {
                                var typeName = schemaType.Name;
                                
                                // Create CompilationUnitSyntax
                                var syntaxFactory = SyntaxFactory.CompilationUnit();

                                // Add System using statement
                                syntaxFactory = syntaxFactory.AddUsings(
                                    SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("GraphQL.Dynamic.Types.LiteralGraphType"))
                                );

                                //  Create a class
                                var classDeclaration = SyntaxFactory.ClassDeclaration(typeName);

                                // Add the public modifier
                                classDeclaration = classDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

                                var name = SyntaxFactory.ParseName("RemoteLiteralGraphTypeMetadata");
                                var arguments = SyntaxFactory.ParseAttributeArgumentList($"(\"{remote.Moniker}\", \"{remote.Url}\", \"{typeName}\")");
                                var attribute = SyntaxFactory.Attribute(name, arguments);

                                var attributeList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute))
                                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                                classDeclaration = classDeclaration.AddAttributeLists(attributeList);

                                classDeclaration = classDeclaration.AddBaseListTypes(
                                    SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("RemoteLiteralGraphType")));

                                var ctorDeclaration = SyntaxFactory.ConstructorDeclaration(typeName)
                                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                    .WithInitializer(
                                        SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer)
                                        .AddArgumentListArguments(
                                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName($"\"{url}\"")),
                                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName($"\"{typeName}\""))
                                        )
                                    )
                                    .WithBody(SyntaxFactory.Block());
                                
                                // Add the field, the property and method to the class.
                                classDeclaration = classDeclaration.AddMembers(ctorDeclaration);
                                
                                syntaxFactory = syntaxFactory.AddMembers(classDeclaration);

                                var referenceAssemblies = CollectReferences();
                                referenceAssemblies.Add(MetadataReference.CreateFromFile(typeof(RemoteLiteralGraphType).Assembly.Location));
                                referenceAssemblies.Add(MetadataReference.CreateFromFile(typeof(ObjectGraphType).Assembly.Location));
                                referenceAssemblies.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

                                var compilation = CSharpCompilation.Create($"GraphQL.Dynamic.RemoteLiteralGraphTypes.{typeName}-{Guid.NewGuid().ToString("N")}",
                                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                                    syntaxTrees: new[] { CSharpSyntaxTree.ParseText(syntaxFactory.NormalizeWhitespace().ToFullString()) },
                                    references: referenceAssemblies
                                    );

                                EmitResult emitResult;

                                using (var ms = new MemoryStream())
                                {
                                    emitResult = compilation.Emit(ms);
                                    if (emitResult.Success)
                                    {
                                        var assembly = Assembly.Load(ms.GetBuffer());
                                        return assembly.GetType(typeName);
                                    } else
                                    {
                                        foreach (var error in emitResult.Diagnostics)
                                            Debug.WriteLine(error.GetMessage());
                                    }
                                }

                                return null;
                            })
                            .ToList();

                        // Update cache
                        var typeSet = new HashSet<Type>(types);
                        RemoteServerTypes.AddOrUpdate(url, typeSet, (key, old) => typeSet);

                        return types;
                    });
                })
                .ToList();

            // Wait for schema & type resolution
            var jaggedTypes = await Task.WhenAll(tasks);

            // Flatten
            return jaggedTypes.SelectMany(t => t).ToList();
        }

        private static List<MetadataReference> CollectReferences()
        {
            // first, collect all assemblies
            var assemblies = new HashSet<Assembly>();

            Collect(Assembly.Load(new AssemblyName("netstandard")));

            // second, build metadata references for these assemblies
            var result = new List<MetadataReference>(assemblies.Count);
            foreach (var assembly in assemblies)
            {
                result.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            return result;

            // helper local function - add assembly and its referenced assemblies
            void Collect(Assembly assembly)
            {
                if (!assemblies.Add(assembly))
                {
                    return;
                }

                var referencedAssemblyNames = assembly.GetReferencedAssemblies();

                foreach (var assemblyName in referencedAssemblyNames)
                {
                    var loadedAssembly = Assembly.Load(assemblyName);
                    assemblies.Add(loadedAssembly);
                }
            }
        }


        private static IEnumerable<FieldType> GetFieldsForFieldType(string remote, Introspection.TypeElement parentField)
        {
            FieldTypeResolver complexFieldTypeResolver = member =>
            {
                if (!(member is RemoteLiteralGraphTypeMemberInfo literalMember))
                {
                    return null;
                }

                if (!RemoteServerTypes.TryGetValue(remote, out var remoteTypes))
                {
                    return null;
                }

                var schemaType = remoteTypes.FirstOrDefault(t => t.Name == literalMember.TypeName);
                var realType = literalMember.IsList
                    ? typeof(ListGraphType<>).MakeGenericType(schemaType)
                    : schemaType;

                return new FieldType
                {
                    Name = literalMember.Name,
                    Type = realType,
                    Resolver = LiteralGraphTypeHelpers.CreateFieldResolverFor(literalMember)
                };
            };

            var fields = parentField.Fields;
            if (fields == null)
            {
                return new FieldType[] { };
            }

            return fields
                .Select(field =>
                {
                    return new RemoteLiteralGraphTypeMemberInfo
                    {
                        DeclaringTypeName = parentField.Name,
                        Name = field.Name,
                        Type = IntrospectionTypeToLiteralGraphTypeMemberInfoType(field.Type),
                        TypeName = IntrospectionTypeToLiteralGraphTypeMemberInfoTypeName(field.Type),
                        IsList = field.Type.Kind == TypeElementTypeKind.List,
                        GetValueFn = ctx =>
                        {
                            return ((JToken)ctx.Source)[field.Name].Value<object>();
                        }
                    };
                })
                // TODO: handle unresolvable types (I'm looking at you UNION)
                .Where(member => member.Type != LiteralGraphTypeMemberInfoType.Unknown)
                .Select(member => LiteralGraphTypeHelpers.GetFieldTypeForMember(member, complexFieldTypeResolver))
                .ToList();
        }

        private static LiteralGraphTypeMemberInfoType IntrospectionTypeToLiteralGraphTypeMemberInfoType(TypeElementType type)
        {
            switch (type.Kind)
            {
                case TypeElementTypeKind.List:
                case TypeElementTypeKind.Object:
                    return LiteralGraphTypeMemberInfoType.Complex;
                case TypeElementTypeKind.Scalar:
                    return ScalarGraphTypeNameToMemberInfoType(type.Name);
                case TypeElementTypeKind.NonNull:
                    return IntrospectionTypeToLiteralGraphTypeMemberInfoType(type.OfType);
                default:
                    return LiteralGraphTypeMemberInfoType.Unknown;
            }
        }

        private static string IntrospectionTypeToLiteralGraphTypeMemberInfoTypeName(TypeElementType type)
        {
            switch (type.Kind)
            {
                case TypeElementTypeKind.Object:
                case TypeElementTypeKind.Scalar:
                    return type.Name;
                case TypeElementTypeKind.List:
                case TypeElementTypeKind.NonNull:
                    return IntrospectionTypeToLiteralGraphTypeMemberInfoTypeName(type.OfType);
                default:
                    return null;
            }
        }

        private static LiteralGraphTypeMemberInfoType ScalarGraphTypeNameToMemberInfoType(string name)
        {
            if (name == "Int")
            {
                return LiteralGraphTypeMemberInfoType.Int;
            }

            if (name == "String")
            {
                return LiteralGraphTypeMemberInfoType.String;
            }

            if (name == "Boolean")
            {
                return LiteralGraphTypeMemberInfoType.Boolean;
            }

            if (name == "Float")
            {
                return LiteralGraphTypeMemberInfoType.Float;
            }

            if (name == "ID")
            {
                return LiteralGraphTypeMemberInfoType.Guid;
            }

            if (name == "Date")
            {
                return LiteralGraphTypeMemberInfoType.DateTime;
            }

            if (name == "Decimal")
            {
                return LiteralGraphTypeMemberInfoType.Long;
            }

            return LiteralGraphTypeMemberInfoType.Unknown;
        }

        private bool TryGetFieldTypeFromRemoteSchema(Introspection.Schema schema, string name, out Introspection.TypeElement field)
        {
            field = schema.Types.FirstOrDefault(t => t.Name == name);

            return field != null;
        }

        private static Introspection.Schema FetchRemoteServerSchema(string remote, RemoteSchemaFetcher remoteSchemaFetcher)
        {
            // lock types mutex
            lock (FetchRemoteServerSchemaMutex)
            {
                // did someone else fetch it while we were waiting?
                if (!RemoteServerSchemas.TryGetValue(remote, out var schema))
                {
                    //  fetch remote type information
                    if (!TryGetRemoteSchema(remote, remoteSchemaFetcher, out schema))
                    {
                        // if it fails: fail
                        // TODO: fail better
                        throw new Exception("Failed to get remote schema");
                    }

                    // if it succeeds: save type information for all types
                    RemoteServerSchemas.AddOrUpdate(remote, schema, (key, old) => schema);
                }

                return schema;
            }
        }

        private static bool TryGetRemoteSchema(string remoteLocation, RemoteSchemaFetcher remoteSchemaFetcher, out Introspection.Schema schema)
        {
            schema = null;

            try
            {
                schema = remoteSchemaFetcher(remoteLocation);
            }
            catch (Exception)
            {
                // TODO: log stuff
            }

            return schema != null;
        }

        private static Introspection.Schema FetchRemoteSchemaViaHttp(string url)
        {
            using (var client = new HttpClient())
            {
                var query = new
                {
                    Query = SchemaIntrospection.IntrospectionQuery
                };

                var response = client.PostAsync(new Uri(url), new StringContent(JsonConvert.SerializeObject(query)))
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                var json = response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                return Introspection.Root.FromJson(json)?.Data?.Schema;
            }
        }

        internal class RemoteLiteralGraphTypeMemberInfo : LiteralGraphTypeMemberInfo
        {
            public string TypeName { get; set; }
        }
    }
}