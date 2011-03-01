﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace BLToolkit.Data.Linq.Parser
{
	using BLToolkit.Linq;
	using Data.Sql;

	class JoinParser : MethodCallParser
	{
		protected override bool CanParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			if (!methodCall.IsQueryable("Join", "GroupJoin") || methodCall.Arguments.Count != 5)
				return false;

			var body = ((LambdaExpression)methodCall.Arguments[2].Unwrap()).Body.Unwrap();

			if (body.NodeType == ExpressionType	.MemberInit)
			{
				var mi = (MemberInitExpression)body;
				bool throwExpr;

				if (mi.NewExpression.Arguments.Count > 0 || mi.Bindings.Count == 0)
					throwExpr = true;
				else
					throwExpr = mi.Bindings.Any(b => b.BindingType != MemberBindingType.Assignment);

				if (throwExpr)
					throw new NotSupportedException(string.Format("Explicit construction of entity type '{0}' in join is not allowed.", body.Type));
			}

			return true;
		}

		protected override IParseContext ParseMethodCall(ExpressionParser parser, IParseContext parent, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			var isGroup      = methodCall.Method.Name == "GroupJoin";
			var outerContext = parser.ParseSequence(parent, methodCall.Arguments[0], sqlQuery);
			var innerContext = parser.ParseSequence(parent, methodCall.Arguments[1], new SqlQuery());

			outerContext = new SubQueryContext(outerContext);
			innerContext = isGroup ? new GroupJoinSubQueryContext(innerContext) : new SubQueryContext(innerContext);;

			var join = innerContext.SqlQuery.InnerJoin();
			var sql  = new SqlQuery();

			sql.From.Table(outerContext.SqlQuery, join);

			var selector = (LambdaExpression)methodCall.Arguments[4].Unwrap();

			outerContext.SetAlias(selector.Parameters[0].Name);
			innerContext.SetAlias(selector.Parameters[1].Name);

			var outerKeyLambda = ((LambdaExpression)methodCall.Arguments[2].Unwrap());
			var innerKeyLambda = ((LambdaExpression)methodCall.Arguments[3].Unwrap());

			var outerKeySelector = outerKeyLambda.Body.Unwrap();
			var innerKeySelector = innerKeyLambda.Body.Unwrap();

			var outerKeyContext = new PathThroughContext(parent, outerContext, outerKeyLambda);
			var innerKeyContext = new PathThroughContext(parent, innerContext, innerKeyLambda);

			if (outerKeySelector.NodeType == ExpressionType.New)
			{
				var new1 = (NewExpression)outerKeySelector;
				var new2 = (NewExpression)innerKeySelector;

				for (var i = 0; i < new1.Arguments.Count; i++)
				{
					var arg1 = new1.Arguments[i];
					var arg2 = new2.Arguments[i];

					var predicate = parser.ParseObjectComparison(ExpressionType.Equal, outerKeyContext, arg1, innerKeyContext, arg2);

					if (predicate != null)
						join.JoinedTable.Condition.Conditions.Add(new SqlQuery.Condition(false, predicate));
					else
						join
							.Expr(parser.ParseExpression(outerKeyContext, arg1)).Equal
							.Expr(parser.ParseExpression(innerKeyContext, arg2));
				}
			}
			else if (outerKeySelector.NodeType == ExpressionType.MemberInit)
			{
				var mi1 = (MemberInitExpression)outerKeySelector;
				var mi2 = (MemberInitExpression)innerKeySelector;

				for (var i = 0; i < mi1.Bindings.Count; i++)
				{
					if (mi1.Bindings[i].Member != mi2.Bindings[i].Member)
						throw new LinqException(string.Format("List of member inits does not match for entity type '{0}'.", outerKeySelector.Type));

					var arg1 = ((MemberAssignment)mi1.Bindings[i]).Expression;
					var arg2 = ((MemberAssignment)mi2.Bindings[i]).Expression;

					var predicate = parser.ParseObjectComparison(ExpressionType.Equal, outerKeyContext, arg1, innerKeyContext, arg2);

					if (predicate != null)
						join.JoinedTable.Condition.Conditions.Add(new SqlQuery.Condition(false, predicate));
					else
						join
							.Expr(parser.ParseExpression(outerKeyContext, arg1)).Equal
							.Expr(parser.ParseExpression(innerKeyContext, arg2));
				}
			}
			else
			{
				var predicate = parser.ParseObjectComparison(
					ExpressionType.Equal,
					outerKeyContext, outerKeySelector,
					innerKeyContext, innerKeySelector);

				if (predicate != null)
					join.JoinedTable.Condition.Conditions.Add(new SqlQuery.Condition(false, predicate));
				else
					join
						.Expr(parser.ParseExpression(outerKeyContext, outerKeySelector)).Equal
						.Expr(parser.ParseExpression(innerKeyContext, innerKeySelector));
			}

			if (isGroup)
			{
				((GroupJoinSubQueryContext)innerContext).Join = join.JoinedTable;
				return new GroupJoinContext(parent, selector, outerContext, innerContext, sql);
			}

			return new JoinContext(parent, selector, outerContext, innerContext, sql);
		}

		internal class JoinContext : SelectContext
		{
			public JoinContext(IParseContext parent, LambdaExpression lambda, IParseContext outerContext, IParseContext innerContext, SqlQuery sql)
				: base(parent, lambda, outerContext, innerContext)
			{
				SqlQuery = sql;
			}

			readonly Dictionary<ISqlExpression,int> _indexes = new Dictionary<ISqlExpression,int>();

			int GetIndex(ISqlExpression sql)
			{
				int idx;

				if (!_indexes.TryGetValue(sql, out idx))
				{
					idx = SqlQuery.Select.Add(sql);
					_indexes.Add(sql, idx);
				}

				return idx;
			}

			public override SqlInfo[] ConvertToIndex(Expression expression, int level, ConvertFlags flags)
			{
				return ConvertToSql(expression, level, flags)
					.Select(i => { i.Index = GetIndex(i.Sql); return i; })
					.ToArray();
			}

			public override int ConvertToParentIndex(int index, IParseContext context)
			{
				var idx = GetIndex(context.SqlQuery.Select.Columns[index]);
				return Parent == null ? idx : Parent.ConvertToParentIndex(idx, this);
			}
		}

		internal class GroupJoinContext : JoinContext
		{
			public GroupJoinContext(IParseContext parent, LambdaExpression lambda, IParseContext outerContext, IParseContext innerContext, SqlQuery sql)
				: base(parent, lambda, outerContext, innerContext, sql)
			{
			}
		}

		internal class GroupJoinSubQueryContext : SubQueryContext
		{
			public SqlQuery.JoinedTable Join;

			public GroupJoinSubQueryContext(IParseContext subQuery) : base(subQuery)
			{
			}

			public override IParseContext GetContext(Expression expression, int level, SqlQuery currentSql)
			{
				if (expression == null)
					return this;

				return base.GetContext(expression, level, currentSql);
			}

			public SqlQuery GetCounter()
			{
				Join.IsWeak = true;

				var visitor = new QueryVisitor();
				var sql     = visitor.Convert(SqlQuery, e =>
				{
					if (e.ElementType == QueryElementType.SqlTable)
					{
						var t = (SqlTable)e;

						return new SqlTable(t);
					}

					return e;
				});

				var sc = new QueryVisitor().Convert(Join.Condition, e =>
				{
					IQueryElement ne;
					return visitor.VisitedElements.TryGetValue(e, out ne) ? ne : e;
				});

				sql.Where.SearchCondition.Conditions.AddRange(sc.Conditions);

				return sql;
			}
		}
	}
}
