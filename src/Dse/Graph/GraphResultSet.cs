﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cassandra;

namespace Dse.Graph
{
    /// <summary>
    /// Represents the result set containing the Graph results returned from a query.
    /// </summary>
    public class GraphResultSet : IEnumerable<GraphResult>
    {
        private readonly RowSet _rs;

        public GraphResultSet(RowSet rs)
        {
            if (rs == null)
            {
                throw new ArgumentNullException("rs");
            }
            _rs = rs;
        }

        public IEnumerator<GraphResult> GetEnumerator()
        {
            return _rs.Select(ParseRow).GetEnumerator();
        }

        private GraphResult ParseRow(Row row)
        {
            return new GraphResult(row.GetValue<string>("gremlin"));
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
