using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>Expression nodes and member contracts, without closure state or compiler names.</summary>
internal static class WorkflowExpressionSignature
{
    public static string Of(LambdaExpression expression, IReadOnlyDictionary<ItemScope, string>? handles = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var shape = new Shape(handles);
        shape.Visit(expression);
        return JsonSerializer.Serialize(shape.Parts);
    }

    private static bool Generated(Type? type) => type?.IsDefined(typeof(CompilerGeneratedAttribute), false) == true;
    private static bool Anonymous(Type? type) => Generated(type) && type!.IsSealed &&
        type.BaseType == typeof(object) && type.GetConstructors().Length == 1 &&
        type.GetProperties().Length > 0 && type.GetProperties().All(property => property.SetMethod == null) &&
        type.GetConstructors()[0].GetParameters().Select(parameter => parameter.Name)
            .SequenceEqual(type.GetProperties().Select(property => property.Name));

    private static string TypeName(Type? type)
    {
        if (type == null) return "";
        if (Anonymous(type))
            return "anonymous:" + JsonSerializer.Serialize(type.GetProperties()
                .Select(property => new { property.Name, Type = TypeName(property.PropertyType) }));
        if (type.GetCustomAttribute<WorkflowAttribute>(inherit: false) is { } identity)
            return "workflow:" + JsonSerializer.Serialize(new { identity.Name, identity.Version });
        if (Generated(type)) return "closure";
        if (type.IsArray) return TypeName(type.GetElementType()) + "[" + type.GetArrayRank() + "]";
        if (type.IsConstructedGenericType)
            return TypeName(type.GetGenericTypeDefinition()) + "<" + string.Join(',', type.GetGenericArguments().Select(TypeName)) + ">";
        return WorkflowTypeIdentity.StableName(type);
    }

    private sealed class Shape(IReadOnlyDictionary<ItemScope, string>? handles) : ExpressionVisitor
    {
        public List<string?> Parts { get; } = [];
        private readonly Dictionary<ParameterExpression, int> _parameters = new();
        private readonly Dictionary<LabelTarget, int> _labels = new();

        public override Expression? Visit(Expression? node)
        {
            Parts.Add(node?.NodeType.ToString());
            Parts.Add(TypeName(node?.Type));
            var result = base.Visit(node);
            Parts.Add("end");
            return result;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!_parameters.TryGetValue(node, out var index))
                _parameters.Add(node, index = _parameters.Count);
            Parts.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            // Literal values belong to the tree. Objects, delegates and closure state do not.
            Parts.Add(node.Value switch
            {
                null => "null",
                string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                    or float or double or decimal => JsonSerializer.Serialize(node.Value, node.Value.GetType()),
                Enum value => value.ToString("D"),
                Type value => TypeName(value),
                ItemScope item => Item(item),
                _ => "object"
            });
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member is FieldInfo field && Generated(field.DeclaringType))
            {
                // A ForEach handle has positional identity. Never serialize captured values or
                // display-class receivers, whose names and layouts depend on the compiler.
                if (typeof(ItemScope).IsAssignableFrom(field.FieldType) && ReadHandle(node) is { } item)
                    Parts.Add(Item(item));
                else
                    Parts.Add("capture");
                return node;
            }
            Member(node.Member);
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Member(node.Method);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            Member(node.Method);
            Parts.Add(node.IsLifted.ToString());
            Parts.Add(node.IsLiftedToNull.ToString());
            return base.VisitBinary(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            Member(node.Method);
            return base.VisitUnary(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            Member(node.Constructor);
            if (node.Members != null)
                foreach (var member in node.Members) Member(member);
            return base.VisitNew(node);
        }

        protected override Expression VisitIndex(IndexExpression node)
        {
            Member(node.Indexer);
            return base.VisitIndex(node);
        }

        protected override Expression VisitTypeBinary(TypeBinaryExpression node)
        {
            Parts.Add(TypeName(node.TypeOperand));
            return base.VisitTypeBinary(node);
        }

        protected override MemberBinding VisitMemberBinding(MemberBinding node)
        {
            Parts.Add(node.BindingType.ToString());
            Member(node.Member);
            return base.VisitMemberBinding(node);
        }

        protected override ElementInit VisitElementInit(ElementInit node)
        {
            Member(node.AddMethod);
            return base.VisitElementInit(node);
        }

        protected override Expression VisitSwitch(SwitchExpression node)
        {
            Member(node.Comparison);
            return base.VisitSwitch(node);
        }

        protected override CatchBlock VisitCatchBlock(CatchBlock node)
        {
            Parts.Add(TypeName(node.Test));
            return base.VisitCatchBlock(node);
        }

        protected override Expression VisitGoto(GotoExpression node)
        {
            Parts.Add(node.Kind.ToString());
            return base.VisitGoto(node);
        }

        protected override LabelTarget? VisitLabelTarget(LabelTarget? node)
        {
            if (node == null) Parts.Add(null);
            else
            {
                if (!_labels.TryGetValue(node, out var index)) _labels.Add(node, index = _labels.Count);
                Parts.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Parts.Add(TypeName(node.Type));
            }
            return node;
        }

        private void Member(MemberInfo? member)
        {
            Parts.Add(member?.MemberType.ToString());
            Parts.Add(TypeName(member?.DeclaringType));
            Parts.Add(!Anonymous(member?.DeclaringType) && (Generated(member?.DeclaringType) ||
                member?.IsDefined(typeof(CompilerGeneratedAttribute), false) == true)
                ? "generated" : member?.Name);
            if (member is MethodBase method)
            {
                Parts.Add(string.Join(";", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))));
                if (method.IsGenericMethod)
                    Parts.Add(string.Join(";", method.GetGenericArguments().Select(TypeName)));
            }
        }

        private string Item(ItemScope item) => handles?.TryGetValue(item, out var id) == true
            ? "item:" + id : "item:" + TypeName(item.ItemType);

        private static ItemScope? ReadHandle(MemberExpression expression) => ReadClosureField(expression) as ItemScope;

        private static object? ReadClosureField(Expression expression) => expression switch
        {
            ConstantExpression constant when Generated(constant.Type) => constant.Value,
            MemberExpression { Member: FieldInfo field, Expression: { } owner } when Generated(field.DeclaringType)
                => ReadClosureField(owner) is { } receiver ? field.GetValue(receiver) : null,
            _ => null
        };
    }
}
