﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ExpressionBuilder.Interfaces;
using ExpressionBuilder.Interfaces.Generics;
using ExpressionBuilder.Helpers;

namespace ExpressionBuilder.Builders
{
	public class FilterBuilder
	{
        readonly BuilderHelper helper;
        
        readonly MethodInfo stringContainsMethod = typeof(string).GetMethod("Contains");
        readonly MethodInfo startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) });
        readonly MethodInfo endsWithMethod = typeof(string).GetMethod("EndsWith", new[] { typeof(string) });

        public readonly Dictionary<Operation, Func<Expression, Expression, Expression, Expression>> Expressions;

        public FilterBuilder(BuilderHelper helper)
		{
            this.helper = helper;

            Expressions = new Dictionary<Operation, Func<Expression, Expression, Expression, Expression>>
            {
                { Operation.Equals, (member, constant, constant2) => Expression.Equal(member, constant) },
                { Operation.NotEquals, (member, constant, constant2) => Expression.NotEqual(member, constant) },
                { Operation.GreaterThan, (member, constant, constant2) => Expression.GreaterThan(member, constant) },
                { Operation.GreaterThanOrEquals, (member, constant, constant2) => Expression.GreaterThanOrEqual(member, constant) },
                { Operation.LessThan, (member, constant, constant2) => Expression.LessThan(member, constant) },
                { Operation.LessThanOrEquals, (member, constant, constant2) => Expression.LessThanOrEqual(member, constant) },
                { Operation.Contains, (member, constant, constant2) => Contains(member, constant) },
                { Operation.StartsWith, (member, constant, constant2) => Expression.Call(member, startsWithMethod, constant) },
                { Operation.EndsWith, (member, constant, constant2) => Expression.Call(member, endsWithMethod, constant) },
                { Operation.Between, (member, constant, constant2) => Between(member, constant, constant2) },
                { Operation.In, (member, constant, constant2) => Contains(member, constant) },
                { Operation.IsNull, (member, constant, constant2) => Expression.Equal(member, Expression.Constant(null)) },
                { Operation.IsNotNull, (member, constant, constant2) => Expression.NotEqual(member, Expression.Constant(null)) },
                { Operation.IsEmpty, (member, constant, constant2) => Expression.Equal(member, Expression.Constant(String.Empty)) },
                { Operation.IsNotEmpty, (member, constant, constant2) => Expression.NotEqual(member, Expression.Constant(String.Empty)) },
                { Operation.IsNullOrWhiteSpace, (member, constant, constant2) => IsNullOrWhiteSpace(member) },
                { Operation.IsNotNullNorWhiteSpace, (member, constant, constant2) => IsNotNullNorWhiteSpace(member) }
            };
        }
		
		public Expression<Func<T, bool>> GetExpression<T>(IFilter filter) where T : class
        {
            var param = Expression.Parameter(typeof(T), "x");
            Expression expression = null;
            var connector = FilterStatementConnector.And;
            foreach (var statement in filter.Statements)
            {
                Expression expr = null;
                if (IsList(statement))
                    expr = ProcessListStatement(param, statement);
                else
                    expr = GetExpression(param, statement);

                expression = expression == null ? expr : CombineExpressions(expression, expr, connector);
                connector = statement.Connector;
            }

            expression = expression ?? Expression.Constant(true);

            return Expression.Lambda<Func<T, bool>>(expression, param);
        }
		
        private bool IsList(IFilterStatement statement)
        {
            return statement.PropertyName.Contains("[") && statement.PropertyName.Contains("]");
        }

        private Expression CombineExpressions(Expression expr1, Expression expr2, FilterStatementConnector connector)
        {
            return connector == FilterStatementConnector.And ? Expression.AndAlso(expr1, expr2) : Expression.OrElse(expr1, expr2);
        }

        private Expression ProcessListStatement(ParameterExpression param, IFilterStatement statement)
        {
            var basePropertyName = statement.PropertyName.Substring(0, statement.PropertyName.IndexOf("["));
            var propertyName = statement.PropertyName.Replace(basePropertyName, "").Replace("[", "").Replace("]", "");

            var type = param.Type.GetProperty(basePropertyName).PropertyType.GetGenericArguments()[0];
            ParameterExpression listItemParam = Expression.Parameter(type, "i");
            var lambda = Expression.Lambda(GetExpression(listItemParam, statement, propertyName), listItemParam);
            var member = helper.GetMemberExpression(param, basePropertyName);
            var enumerableType = typeof(Enumerable);
            var anyInfo = enumerableType.GetMethods(BindingFlags.Static | BindingFlags.Public).First(m => m.Name == "Any" && m.GetParameters().Count() == 2);
            anyInfo = anyInfo.MakeGenericMethod(type);
            return Expression.Call(anyInfo, member, lambda);
        }
        
        private Expression GetExpression(ParameterExpression param, IFilterStatement statement, string propertyName = null)
        {
            var memberName = propertyName ?? statement.PropertyName;
            Expression member = helper.GetMemberExpression(param, memberName);
            Expression constant = GetConstantExpression(member, statement.Value);
            Expression constant2 = GetConstantExpression(member, statement.Value2);
            
            Expression resultExpr = GetSafeStringExpression(member, statement.Operation, constant, constant2);
            resultExpr = GetSafePropertyMember(param, memberName, resultExpr);

            return resultExpr;
        }

        private Expression GetSafeStringExpression(Expression member, Operation operation, Expression constant, Expression constant2)
        {
            if (member.Type != typeof(string))
            {
                return Expressions[operation].Invoke(member, constant, constant2);
            }

            Expression newMember = member;

            if (operation != Operation.IsNullOrWhiteSpace && operation != Operation.IsNotNullNorWhiteSpace)
            {
                var trimMemberCall = Expression.Call(member, helper.trimMethod);
                newMember = Expression.Call(trimMemberCall, helper.toLowerMethod);
            }

            Expression resultExpr = Expressions[operation].Invoke(newMember, constant, constant2);

            if (member.Type == typeof(string))
            {
                if (operation != Operation.IsNullOrWhiteSpace && operation != Operation.IsNotNullNorWhiteSpace)
                {
                    Expression memberIsNotNull = Expression.NotEqual(member, Expression.Constant(null));
                    resultExpr = Expression.AndAlso(memberIsNotNull, resultExpr);
                }
            }

            return resultExpr;
        }

        public Expression GetSafePropertyMember(ParameterExpression param, String memberName, Expression expr)
        {
            if (!memberName.Contains("."))
            {
                return expr;
            }

            string parentName = memberName.Substring(0, memberName.IndexOf("."));
            Expression parentMember = helper.GetMemberExpression(param, parentName);
            return Expression.AndAlso(Expression.NotEqual(parentMember, Expression.Constant(null)), expr);
        }

        private Expression GetConstantExpression(Expression member, object value)
        {
            if (value == null) return null;

            Expression constant = Expression.Constant(value);

            if (value is string)
            {
                var trimConstantCall = Expression.Call(constant, helper.trimMethod);
                constant = Expression.Call(trimConstantCall, helper.toLowerMethod);
            }

            return constant;
        }

        #region Operations 
        private Expression Contains(Expression member, Expression expression)
        {
            MethodCallExpression contains = null;
            if (expression is ConstantExpression constant && constant.Value is IList && constant.Value.GetType().IsGenericType)
            {
                var type = constant.Value.GetType();
                var containsInfo = type.GetMethod("Contains", new[] { type.GetGenericArguments()[0] });
                contains = Expression.Call(constant, containsInfo, member);
            }

            return contains ?? Expression.Call(member, stringContainsMethod, expression); ;
        }

        private Expression Between(Expression member, Expression constant, Expression constant2)
        {
            var left = Expressions[Operation.GreaterThanOrEquals].Invoke(member, constant, null);
            var right = Expressions[Operation.LessThanOrEquals].Invoke(member, constant2, null);

            return CombineExpressions(left, right, FilterStatementConnector.And);
        }

        private Expression IsNullOrWhiteSpace(Expression member)
        {
            Expression exprNull = Expression.Constant(null);
            var trimMemberCall = Expression.Call(member, helper.trimMethod);
            Expression exprEmpty = Expression.Constant(string.Empty);
            return Expression.OrElse(
                                    Expression.Equal(member, exprNull),
                                    Expression.Equal(trimMemberCall, exprEmpty));
        }

        private Expression IsNotNullNorWhiteSpace(Expression member)
        {
            Expression exprNull = Expression.Constant(null);
            var trimMemberCall = Expression.Call(member, helper.trimMethod);
            Expression exprEmpty = Expression.Constant(string.Empty);
            return Expression.AndAlso(
                                    Expression.NotEqual(member, exprNull),
                                    Expression.NotEqual(trimMemberCall, exprEmpty));
        }
        #endregion
    }
}