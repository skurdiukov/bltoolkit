﻿using System;
using System.Linq.Expressions;

namespace BLToolkit.Data.Linq.Parser
{
	using BLToolkit.Linq;
	using Data.Sql;

	class SelectManyParser : MethodCallParser
	{
		protected override bool CanParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			return
				methodCall.IsQueryable("SelectMany") &&
				methodCall.Arguments.Count == 3      &&
				((LambdaExpression)methodCall.Arguments[1].Unwrap()).Parameters.Count == 1;
		}

		protected override IParseContext ParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			var sequence           = parser.ParseSequence(methodCall.Arguments[0], sqlQuery);
			var collectionSelector = (LambdaExpression)methodCall.Arguments[1].Unwrap();
			var resultSelector     = (LambdaExpression)methodCall.Arguments[2].Unwrap();

			//if (collectionSelector.Parameters[0] == collectionSelector.Body.Unwrap())
			//{
			//	return resultSelector == null ? sequence : new SelectContext(resultSelector, sequence, sequence);
			//}

			var context    = new SelectManyContext(collectionSelector, sequence);
			var expr       = collectionSelector.Body.Unwrap();
			var crossApply = null != expr.Find(e => e == collectionSelector.Parameters[0]);

			/*
			if (expr.NodeType == ExpressionType.Call)
			{
				var call = (MethodCallExpression)collectionSelector.Body;

				if (call.IsQueryable("DefaultIfEmpty"))
				{
					leftJoin = true;
					expr     = call.Arguments[0];
				}
			}
			*/

			parser.ParentContext.Insert(0, context);
			parser.SubQueryParsingCounter++;

			var collection = parser.ParseSequence(expr, new SqlQuery());
			var leftJoin   = collection is DefaultIfEmptyParser.DefaultIfEmptyContext;

			parser.SubQueryParsingCounter--;
			//parser.ParentContext.RemoveAt(0);

			var sql = collection.SqlQuery;

			if (!leftJoin && crossApply)
			{
				if (sql.GroupBy.IsEmpty &&
					sql.Select.Columns.Count == 0 &&
					!sql.Select.HasModifier &&
					sql.Where.IsEmpty &&
					!sql.HasUnion &&
					sql.From.Tables.Count == 1)
				{
					crossApply = false;
				}
			}

			if (!leftJoin && !crossApply)
			{
				sequence.SqlQuery.From.Table(sql);

				//sql.ParentSql = sequence.SqlQuery;

				var col = (IParseContext)new SubQueryContext(collection, sequence.SqlQuery, true);

				return new SelectContext(resultSelector, context, col);
				return new SelectContext(resultSelector, sequence, col);
			}

			//if (crossApply)
			{
				if (sql.GroupBy.IsEmpty &&
					sql.Select.Columns.Count == 0 &&
					!sql.Select.HasModifier &&
					//!sql.Where.IsEmpty &&
					!sql.HasUnion && sql.From.Tables.Count == 1)
				{
					var join = leftJoin ? SqlQuery.LeftJoin(sql) : SqlQuery.InnerJoin(sql);

					join.JoinedTable.Condition.Conditions.AddRange(sql.Where.SearchCondition.Conditions);

					sql.Where.SearchCondition.Conditions.Clear();

					if (collection is TableParser.TableContext)
					{
						var parent = collection.Parent as TableParser.TableContext;

						if (parent != null)
						{
							var ts     = (SqlQuery.TableSource)new QueryVisitor().Find(sequence.SqlQuery.From, e =>
							{
								if (e.ElementType == QueryElementType.TableSource)
								{
									var t = (SqlQuery.TableSource)e;
									return t.Source == parent.SqlTable;
								}

								return false;
							});

							ts.Joins.Add(join.JoinedTable);
						}
						else
						{
							sequence.SqlQuery.From.Tables[0].Joins.Add(join.JoinedTable);
						}
					}
					else
					{
						sequence.SqlQuery.From.Tables[0].Joins.Add(join.JoinedTable);
						//collection.SqlQuery = sequence.SqlQuery;
					}

					//sql.ParentSql = sequence.SqlQuery;

					var col = (IParseContext)new SubQueryContext(collection, sequence.SqlQuery, false);

					return new SelectContext(resultSelector, context, col);
					return new SelectContext(resultSelector, sequence, col);
				}
			}

			throw new LinqException("Sequence '{0}' cannot be converted to SQL.", expr);
		}

		public class SelectManyContext : SelectContext
		{
			public SelectManyContext(LambdaExpression lambda, IParseContext sequence)
				: base(lambda, sequence)
			{
			}

			/*
			public override bool IsExpression(Expression expression, int level, RequestFor requestFlag)
			{
				if (expression == null || level == 0 && expression == Body)
				{
					switch (requestFlag)
					{
						case RequestFor.Object      : return true;
						case RequestFor.Association :
						case RequestFor.Field       :
						case RequestFor.Expression  : return false;
					}
				}

				return base.IsExpression(expression, level, requestFlag);
			}
			*/
		}
	}
}