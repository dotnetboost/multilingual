using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>The request's services — the scoped provider a context's options were built from.</summary>
internal static class MultiLingualServices
{
    public static IServiceProvider For(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
        ?? throw new InvalidOperationException(
            $"'{context.GetType().Name}' has no MultiLingual services. Register it with " +
            "AddDbContext((sp, options) => options.UseMultiLingual(sp)).");
}

/// <summary>
/// Placeholders for the languages a query reads in. EF compiles a query once per shape and reuses
/// it for every request, so the compiled SQL cannot contain the request's language. It contains
/// these instead, as string literals, and <see cref="LanguageTokenInterceptor"/> replaces them in
/// the command text just before each execution.
/// </summary>
internal static partial class LanguageTokens
{
    /// <summary>Every token starts with it; a command without it is left alone.</summary>
    public const string Prefix = "~ml:";

    /// <summary>The request's language.</summary>
    public const string Language = "~ml:L~";

    /// <summary>The request language's fallback.</summary>
    public const string Fallback = "~ml:F~";

    /// <summary>The fallback of an explicit language (<c>InLanguage</c>).</summary>
    public static string FallbackFor(string language) => $"~ml:F:{language}~";

    [GeneratedRegex(@"~ml:(?<kind>[LF])(?::(?<code>[A-Za-z0-9-]+))?~", RegexOptions.CultureInvariant)]
    public static partial Regex Pattern();
}

/// <summary>
/// What <see cref="MultiLingualQueryInterceptor"/> does to a query when EF compiles it:
/// <list type="number">
///   <item>Every read of a translatable property — in <c>Where</c>, <c>OrderBy</c>, <c>Select</c>,
///   through navigations and nested collections — becomes
///   <c>COALESCE(lookup(language), lookup(fallback), base column)</c>.</item>
///   <item>A query that returns entities gets a final projection that loads, in the same
///   statement, each entity's translated values and those of its included navigations, and hands
///   them to <see cref="EntityFill"/>.</item>
/// </list>
/// The languages are <see cref="LanguageTokens"/>, so one compiled query serves every language.
/// Entity and property names never vary, so they are literals.
/// </summary>
internal static class QueryTranslation
{
    /// <summary>One translation value: <c>translations.Where(key matches).Select(t =&gt; t.Value).FirstOrDefault()</c>.</summary>
    private static readonly Expression<Func<IQueryable<Translation>, string, string, string, string, string?>> LookupTemplate =
        (translations, entityName, propertyName, languageCode, entityId) => translations
            .Where(t => t.EntityName == entityName
                        && t.PropertyName == propertyName
                        && t.LanguageCode == languageCode
                        && t.EntityId == entityId)
            .Select(t => t.Value)
            .FirstOrDefault();

    private static readonly MethodInfo QueryableSelect = QueryableMethod(nameof(Queryable.Select), 2, 2);
    private static readonly MethodInfo QueryableWhere  = QueryableMethod(nameof(Queryable.Where), 1, 2);
    private static readonly MethodInfo EnumerableSelect = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Select) && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);
    private static readonly MethodInfo EnumerableToList = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!;

    /// <summary>Terminal operators whose result is one element of the sequence.</summary>
    private static readonly HashSet<string> SingleElementOperators =
    [
        nameof(Queryable.First), nameof(Queryable.FirstOrDefault),
        nameof(Queryable.Single), nameof(Queryable.SingleOrDefault),
        nameof(Queryable.Last), nameof(Queryable.LastOrDefault),
        nameof(Queryable.ElementAt), nameof(Queryable.ElementAtOrDefault)
    ];

    private static readonly ConcurrentDictionary<IEntityType, bool> Reaches = new();
    private static readonly ConcurrentDictionary<(IModel, Type), IReadOnlyList<HierarchyProperty>> Hierarchies = new();

    /// <summary>
    /// Translates <paramref name="query"/>. Returns it unchanged — markers aside — when it opts out
    /// with <c>IgnoreTranslations()</c>, or touches nothing translatable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query names two different languages.</exception>
    /// <exception cref="UnknownLanguageException">Its <c>InLanguage</c> code does not exist.</exception>
    public static Expression Rewrite(Expression query, DbContext context)
    {
        var markers = new MarkerRemover();
        query = markers.Visit(query);

        if (markers.Ignore)
            return query;

        var services = MultiLingualServices.For(context);
        var options  = services.GetRequiredService<MultiLingualOptions>();

        string? language = null;
        if (markers.Languages.Count > 1)
        {
            throw new InvalidOperationException(
                $"This query names more than one language with InLanguage ({string.Join(", ", markers.Languages)}). " +
                "A query reads in one language.");
        }

        if (markers.Languages.Count == 1)
        {
            // Synchronous, and it has to be: EF calls this while COMPILING the query, through an
            // API that cannot be awaited. It is also once per query shape rather than once per
            // execution, so the blocking catalog read cannot recur under load the way a
            // per-execution one would — see LanguageTokenInterceptor.ApplyAsync.
            language = services.GetRequiredService<TranslationManager>().ReadLanguages(markers.Languages.Single()).Language;
        }

        var chain = new Chain(context, options, language);
        query = new ReadTranslator(chain).Visit(query);
        return new ResultFiller(chain).Wrap(query);
    }

    /// <summary>A translatable property, and the entity class that declares it.</summary>
    internal sealed record HierarchyProperty(Type Owner, TranslatableProperty Property);

    /// <summary>
    /// The translatable properties an entity queried as <paramref name="declared"/> can have: those
    /// of <paramref name="declared"/>, then those only its derived entity types add — a query over
    /// the base of a hierarchy returns derived entities too. The order is the order of the values
    /// a query loads, at compile time and when they are applied alike.
    /// </summary>
    internal static IReadOnlyList<HierarchyProperty> Hierarchy(IModel model, Type declared) =>
        Hierarchies.GetOrAdd((model, MultiLingualMetadata.EntityClass(declared)), static key =>
        {
            var (model, type) = key;
            var properties = MultiLingualMetadata.For(type).Select(p => new HierarchyProperty(type, p)).ToList();
            var names = properties.Select(p => p.Property.Name).ToHashSet(StringComparer.Ordinal);

            var derived = model.FindEntityType(type)?.GetDerivedTypes()
                              .Select(t => t.ClrType)
                              .OrderBy(t => t.FullName, StringComparer.Ordinal)
                          ?? Enumerable.Empty<Type>();

            foreach (var derivedType in derived)
            {
                foreach (var property in MultiLingualMetadata.For(derivedType))
                {
                    if (names.Add(property.Name))
                        properties.Add(new HierarchyProperty(derivedType, property));
                }
            }

            return properties;
        });

    /// <summary>True when entities of <paramref name="entityType"/> can hold translated values, or lead to some.</summary>
    public static bool ReachesTranslations(IEntityType entityType) =>
        Reaches.GetOrAdd(entityType, static type => Search(type, []));

    private static bool Search(IEntityType type, HashSet<IEntityType> seen)
    {
        if (!seen.Add(type))
            return false;

        if (MultiLingualMetadata.IsMultiLingual(type.ClrType))
            return true;

        return type.GetNavigations().Select(n => n.TargetEntityType)
            .Concat(type.GetSkipNavigations().Select(n => n.TargetEntityType))
            .Any(target => Search(target, seen));
    }

    // ================================================================== the value of one property

    /// <summary>The fallback chain of one compiled query.</summary>
    private sealed class Chain(DbContext context, MultiLingualOptions options, string? language)
    {
        public DbContext Context => context;

        public IModel Model => context.Model;

        /// <summary>The explicit language, canonical; null for the request's.</summary>
        public string? Language => language;

        private Expression LanguageCode => Expression.Constant(
            language is null ? LanguageTokens.Language : options.IsBaseLanguage(language) ? "" : language);

        private Expression? FallbackCode => options.FallbackToDefaultLanguage
            ? Expression.Constant(language is null ? LanguageTokens.Fallback : LanguageTokens.FallbackFor(language))
            : null;

        /// <summary>The translated value of <paramref name="member"/> on <paramref name="entity"/>.</summary>
        public Expression Read(Expression entity, MemberInfo member)
        {
            var chain = new List<Expression> { Lookup(entity, member.Name, LanguageCode) };

            if (FallbackCode is { } fallback)
                chain.Add(Lookup(entity, member.Name, fallback));

            if (options.HasBaseLanguage)
                chain.Add(Expression.MakeMemberAccess(entity, member));

            var body = chain[^1];
            for (var i = chain.Count - 2; i >= 0; i--)
                body = Expression.Coalesce(chain[i], body);

            return body;
        }

        /// <summary>
        /// Two per property of <see cref="Hierarchy"/>: language, then fallback. A derived type's
        /// own property is looked up under that type's entity name, with the key the whole
        /// hierarchy shares.
        /// </summary>
        public NewArrayExpression Values(Expression entity, Type declared) =>
            Expression.NewArrayInit(typeof(string), Hierarchy(Model, declared).SelectMany(p => new[]
            {
                Lookup(entity, p.Property.Name, LanguageCode, p.Owner),
                FallbackCode is { } fallback ? Lookup(entity, p.Property.Name, fallback, p.Owner) : Expression.Constant(null, typeof(string))
            }));

        private Expression Lookup(Expression entity, string propertyName, Expression code, Type? owner = null)
        {
            var parameters = LookupTemplate.Parameters;
            return new Replacer(new Dictionary<Expression, Expression>
            {
                [parameters[0]] = context.Set<Translation>().AsQueryable().Expression,
                [parameters[1]] = Expression.Constant(MultiLingualMetadata.EntityNameFor(owner ?? entity.Type)),
                [parameters[2]] = Expression.Constant(propertyName),
                [parameters[3]] = code,
                [parameters[4]] = EntityKeys.KeyAsString(context, entity)
            }).Visit(LookupTemplate.Body)!;
        }
    }

    // ================================================================== reads anywhere in the query

    /// <summary>
    /// Replaces every read of a translatable property of an entity — on a lambda parameter,
    /// through a navigation, or inside a nested collection projection — with its translated value.
    /// Translatable-looking properties on types that are not entities of this model are left alone.
    /// </summary>
    private sealed class ReadTranslator(Chain chain) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // ExecuteUpdate's SetProperty(p => p.Name, value): the first argument names the column
            // to write, so it must stay a column. Only the value is translated. Matched by name:
            // the setter type is SetPropertyCalls<T> in EF Core 8 and UpdateSettersBuilder<T> later.
            if (node.Method.Name == "SetProperty" && node.Arguments.Count == 2)
                return node.Update(Visit(node.Object), [node.Arguments[0], Visit(node.Arguments[1])]);

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is { } owner
                && node.Member is PropertyInfo { PropertyType: var type }
                && type == typeof(string)
                && chain.Model.FindEntityType(owner.Type) is not null
                && MultiLingualMetadata.For(owner.Type).Any(p => string.Equals(p.Name, node.Member.Name, StringComparison.Ordinal)))
            {
                return chain.Read(Visit(owner), node.Member);
            }

            return base.VisitMember(node);
        }
    }

    // ================================================================== entities in the result

    /// <summary>
    /// Adds the final projection that fills the entities a query returns: the entity itself (a
    /// sequence, or First / Single / … of one), or entities placed in a final <c>Select</c>.
    /// </summary>
    private sealed class ResultFiller(Chain chain)
    {
        public Expression Wrap(Expression query)
        {
            if (query is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable) && call.Arguments.Count > 0)
            {
                // First / Single / ElementAt … of an entity sequence.
                if (SingleElementOperators.Contains(call.Method.Name) && ElementType(call.Arguments[0]) == call.Type)
                {
                    if (Fillable(call.Type))
                        return WrapSingle(call);

                    // … of a projection: fill the entities inside it.
                    if (call.Arguments[0] is MethodCallExpression { Method.Name: nameof(Queryable.Select) } select
                        && select.Method.DeclaringType == typeof(Queryable))
                    {
                        return call.Update(call.Object, [FillProjection(select), .. call.Arguments.Skip(1)]);
                    }
                }
            }

            if (ElementType(query) is not { } element)
                return query;

            if (Fillable(element))
                return WrapSequence(query, element);

            return query is MethodCallExpression { Method.Name: nameof(Queryable.Select) } last && last.Method.DeclaringType == typeof(Queryable)
                ? FillProjection(last)
                : query;
        }

        private bool Fillable(Type type) =>
            chain.Model.FindEntityType(type) is { } entityType && ReachesTranslations(entityType);

        /// <summary><c>First(source, predicate)</c> → <c>First(source.Where(predicate).Select(fill))</c>.</summary>
        private MethodCallExpression WrapSingle(MethodCallExpression call)
        {
            var element   = call.Type;
            var source    = call.Arguments[0];
            var rest      = call.Arguments.Skip(1).ToList();
            var predicate = rest.FirstOrDefault(a => a is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression { ReturnType: var r } } && r == typeof(bool));

            if (predicate is not null)
            {
                source = Expression.Call(QueryableWhere.MakeGenericMethod(element), source, predicate);
                rest.Remove(predicate);
            }

            var method = typeof(Queryable).GetMethods()
                .Where(m => m.Name == call.Method.Name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1)
                .Select(m => m.MakeGenericMethod(element))
                .First(m =>
                {
                    var parameters = m.GetParameters();
                    return parameters.Length == rest.Count + 1
                           && parameters.Skip(1).Zip(rest).All(pair => pair.First.ParameterType.IsAssignableFrom(pair.Second.Type));
                });

            return Expression.Call(method, [WrapSequence(source, element), .. rest]);
        }

        private Expression WrapSequence(Expression source, Type element)
        {
            var includes = Includes.Of(source, element, chain.Model);
            var entity   = Expression.Parameter(element, "e");

            if (ValuesFor(entity, element, includes, key: false) is not { } values)
                return source;

            var fill = Expression.Call(EntityFill.FillMethod.MakeGenericMethod(element), entity, values,
                Expression.Constant(chain.Language, typeof(string)));

            return Expression.Call(QueryableSelect.MakeGenericMethod(element, element), source,
                Expression.Quote(Expression.Lambda(fill, entity)));
        }

        /// <summary>
        /// <c>new LoadedValues { Values = …, Navigations = … }</c> for <paramref name="entity"/>, or
        /// null when it and the navigations included under it hold nothing translatable.
        /// </summary>
        private MemberInitExpression? ValuesFor(Expression entity, Type declared, Includes.Node includes, bool key)
        {
            var bindings = new List<MemberBinding>();

            if (Hierarchy(chain.Model, declared).Count > 0)
                bindings.Add(Expression.Bind(Member<LoadedValues>(nameof(LoadedValues.Values)), chain.Values(entity, declared)));

            var entityType  = chain.Model.FindEntityType(declared);
            var navigations = new List<Expression>();

            foreach (var child in includes.Children.Values)
            {
                INavigationBase? navigation = (INavigationBase?)entityType?.FindNavigation(child.Name) ?? entityType?.FindSkipNavigation(child.Name);
                if (navigation is null)
                    continue;

                var target = navigation.TargetEntityType.ClrType;
                var source = child.Source(entity);

                if (!navigation.IsCollection)
                {
                    if (ValuesFor(source, target, child, key: false) is { } single)
                        navigations.Add(Navigation(child.Name, nameof(NavigationValues.Single), single));

                    continue;
                }

                var item = Expression.Parameter(target, "i");
                if (ValuesFor(item, target, child, key: true) is not { } itemValues)
                    continue;

                var items = Expression.Call(EnumerableToList.MakeGenericMethod(typeof(LoadedValues)),
                    Expression.Call(EnumerableSelect.MakeGenericMethod(target, typeof(LoadedValues)), source, Expression.Lambda(itemValues, item)));

                navigations.Add(Navigation(child.Name, nameof(NavigationValues.Items), items));
            }

            if (navigations.Count > 0)
            {
                bindings.Add(Expression.Bind(Member<LoadedValues>(nameof(LoadedValues.Navigations)),
                    Expression.NewArrayInit(typeof(NavigationValues), navigations)));
            }

            if (bindings.Count == 0)
                return null;

            if (key)
                bindings.Add(Expression.Bind(Member<LoadedValues>(nameof(LoadedValues.Key)), EntityKeys.KeyAsString(chain.Context, entity)));

            return Expression.MemberInit(Expression.New(typeof(LoadedValues)), bindings);
        }

        private static MemberInitExpression Navigation(string name, string slot, Expression values) =>
            Expression.MemberInit(Expression.New(typeof(NavigationValues)),
                Expression.Bind(Member<NavigationValues>(nameof(NavigationValues.Name)), Expression.Constant(name)),
                Expression.Bind(Member<NavigationValues>(slot), values));

        /// <summary>A final <c>Select</c>: fills the entities it places in its result.</summary>
        private MethodCallExpression FillProjection(MethodCallExpression select)
        {
            if (select.Arguments[1] is not UnaryExpression { Operand: LambdaExpression lambda })
                return select;

            var body = new ProjectionFiller(this).Visit(lambda.Body)!;
            return body == lambda.Body
                ? select
                : select.Update(select.Object, [select.Arguments[0], Expression.Quote(Expression.Lambda(lambda.Type, body, lambda.Parameters))]);
        }

        /// <summary>The fill of one entity found in a projection — without includes.</summary>
        private MethodCallExpression? FillOne(Expression entity)
        {
            if (chain.Model.FindEntityType(entity.Type) is null
                || Hierarchy(chain.Model, entity.Type).Count == 0
                || ValuesFor(entity, entity.Type, Includes.Node.Empty, key: false) is not { } values)
            {
                return null;
            }

            return Expression.Call(EntityFill.FillMethod.MakeGenericMethod(entity.Type), entity, values,
                Expression.Constant(chain.Language, typeof(string)));
        }

        /// <summary>
        /// Walks the structure of a projection — <c>new { … }</c>, object initializers, arrays,
        /// conditionals — and fills each entity it places there. It does not look inside member
        /// owners (<c>p</c> in <c>p.Sku</c>), method calls or nested lambdas.
        /// </summary>
        private sealed class ProjectionFiller(ResultFiller filler) : ExpressionVisitor
        {
            public override Expression? Visit(Expression? node) => node switch
            {
                null => null,
                ParameterExpression or MemberExpression => filler.FillOne(node) ?? node,
                NewExpression or MemberInitExpression or NewArrayExpression or ConditionalExpression
                    or UnaryExpression { NodeType: ExpressionType.Convert } => base.Visit(node),
                _ => node
            };

            protected override Expression VisitMemberInit(MemberInitExpression node) =>
                node.Update(node.NewExpression, node.Bindings.Select(b => b is MemberAssignment assignment
                    ? assignment.Update(Visit(assignment.Expression)!)
                    : b));
        }
    }

    // ================================================================== what the query includes

    /// <summary>The navigations a query includes, as a tree — explicit and automatic.</summary>
    private static class Includes
    {
        public sealed class Node
        {
            public static readonly Node Empty = new("", null);

            private readonly LambdaExpression? _source;

            public Node(string name, LambdaExpression? source)
            {
                Name    = name;
                _source = source;
            }

            public string Name { get; }

            public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

            /// <summary>The navigation's value from <paramref name="parent"/> — filtered, when the include was.</summary>
            public Expression Source(Expression parent) => _source is null
                ? Expression.PropertyOrField(parent, Name)
                : new Replacer(new Dictionary<Expression, Expression> { [_source.Parameters[0]] = parent }).Visit(_source.Body)!;

            public Node Add(string name, LambdaExpression? source)
            {
                if (!Children.TryGetValue(name, out var child))
                {
                    child = new Node(name, source);
                    Children[name] = child;
                }

                return child;
            }
        }

        public static Node Of(Expression source, Type element, IModel model)
        {
            // The operators applied to the sequence while its element type is still the entity,
            // outermost first.
            var calls = new List<MethodCallExpression>();
            var current = source;
            while (current is MethodCallExpression call && call.Arguments.Count > 0 && ElementType(call.Arguments[0]) == element)
            {
                calls.Add(call);
                current = call.Arguments[0];
            }

            var root = new Node("", null);
            Node? last = null;

            foreach (var call in Enumerable.Reverse(calls))
            {
                if (call.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions))
                    continue;

                switch (call.Method.Name)
                {
                    case nameof(EntityFrameworkQueryableExtensions.Include) when call.Arguments[1] is ConstantExpression { Value: string path }:
                        last = root;
                        foreach (var name in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            last = last.Add(name, null);
                        break;

                    case nameof(EntityFrameworkQueryableExtensions.Include) when Lambda(call.Arguments[1]) is { } include:
                        last = NavigationName(include.Body) is { } includeName ? root.Add(includeName, include) : null;
                        break;

                    case nameof(EntityFrameworkQueryableExtensions.ThenInclude) when last is not null && Lambda(call.Arguments[1]) is { } then:
                        last = NavigationName(then.Body) is { } thenName ? last.Add(thenName, then) : null;
                        break;
                }
            }

            var ignoreAutoIncludes = calls.Any(c =>
                c.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && c.Method.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes));

            if (!ignoreAutoIncludes && model.FindEntityType(element) is { } entityType)
                AddAutoIncludes(root, entityType, depth: 0);

            return root;
        }

        private static void AddAutoIncludes(Node node, IEntityType entityType, int depth)
        {
            if (depth > 8)
                return;

            foreach (var navigation in entityType.GetNavigations().Cast<INavigationBase>().Concat(entityType.GetSkipNavigations()))
            {
                if (navigation.IsEagerLoaded)
                    node.Add(navigation.Name, null);
            }

            foreach (var child in node.Children.Values)
            {
                INavigationBase? navigation = (INavigationBase?)entityType.FindNavigation(child.Name) ?? entityType.FindSkipNavigation(child.Name);
                if (navigation is not null)
                    AddAutoIncludes(child, navigation.TargetEntityType, depth + 1);
            }
        }

        private static LambdaExpression? Lambda(Expression argument) => argument switch
        {
            UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda } => lambda,
            LambdaExpression lambda => lambda,
            _ => null
        };

        /// <summary><c>e.Products</c>, <c>((Derived)e).Products</c>, <c>e.Products.Where(…).OrderBy(…)</c> → "Products".</summary>
        private static string? NavigationName(Expression body)
        {
            while (true)
            {
                switch (body)
                {
                    case MethodCallExpression { Arguments.Count: > 0 } call when call.Method.IsStatic:
                        body = call.Arguments[0];
                        continue;
                    case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary:
                        body = unary.Operand;
                        continue;
                    case MemberExpression member:
                        return member.Member.Name;
                    default:
                        return null;
                }
            }
        }
    }

    // ================================================================== helpers

    /// <summary>The element type of a query expression, or null when it is not a query.</summary>
    private static Type? ElementType(Expression expression)
    {
        if (!typeof(IQueryable).IsAssignableFrom(expression.Type))
            return null;

        var queryable = expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == typeof(IQueryable<>)
            ? expression.Type
            : expression.Type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>));

        return queryable?.GetGenericArguments()[0];
    }

    private static MethodInfo QueryableMethod(string name, int genericArguments, int parameters) =>
        typeof(Queryable).GetMethods().Single(m =>
            m.Name == name
            && m.GetGenericArguments().Length == genericArguments
            && m.GetParameters().Length == parameters
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static PropertyInfo Member<T>(string name) => typeof(T).GetProperty(name)!;

    /// <summary>Removes the per-query markers, remembering what they asked for.</summary>
    private sealed class MarkerRemover : ExpressionVisitor
    {
        public bool Ignore { get; private set; }

        public SortedSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod)
            {
                var definition = node.Method.GetGenericMethodDefinition();

                if (definition == MultiLingualQueryableExtensions.IgnoreTranslationsMethod)
                {
                    Ignore = true;
                    return Visit(node.Arguments[0]);
                }

                if (definition == MultiLingualQueryableExtensions.InLanguageMethod)
                {
                    if (node.Arguments[1] is ConstantExpression { Value: string code })
                        Languages.Add(code);

                    return Visit(node.Arguments[0]);
                }
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class Replacer(Dictionary<Expression, Expression> replacements) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) =>
            node is not null && replacements.TryGetValue(node, out var replacement) ? replacement : base.Visit(node);
    }
}
