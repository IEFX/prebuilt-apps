using System;
using Novell.Directory.Ldap;
using Novell.Directory.Ldap.Utilclass;
using System.Threading.Tasks;
using System.Collections.Generic;
using EmployeeDirectory.Data;
using System.Linq;
using System.Reflection;

namespace EmployeeDirectory
{
	/// <summary>
	/// Minimal implementation of an LDAP directory service.
	/// </summary>
	public class LdapDirectoryService : IDirectoryService
	{
		public string Host { get; set; }

		public int Port { get; set; }

		public string SearchBase { get; set; }

		public LdapDirectoryService ()
		{
			Host = "";
			Port = 389;
			SearchBase = "";
		}

		public Task<IList<Person>> SearchAsync (Filter filter, int sizeLimit)
		{
			ValidateConfiguration ();

			//
			// Compile the filter
			//
			var compiledFilter = CompileFilter (filter);
			if (string.IsNullOrEmpty (compiledFilter)) {
				compiledFilter = "(objectClass=*)";
			}

			//
			// Since the LDAP library doesn't support async, wrap it
			// in a new task.
			//
			return Task.Factory.StartNew (() => {
				//
				// Search
				//
				return Search (compiledFilter, sizeLimit);
			});
		}

		/// <summary>
		/// Compiles the filter.
		/// </summary>
		/// <remarks>
		/// <a href="http://msdn.microsoft.com/en-us/library/windows/desktop/aa746475(v=vs.85).aspx">Search
		/// Filter Syntax</a>
		/// </remarks>
		/// <returns>
		/// The filter.
		/// </returns>
		/// <param name='filter'>
		/// Filter.
		/// </param>
		string CompileFilter (Filter filter)
		{
			if (filter == null) return "";

			if (filter is AndFilter) {
				return "(&" + string.Join ("", ((AndFilter)filter).Filters.Select (CompileFilter)) + ")";
			}
			else if (filter is OrFilter) {
				return "(|" + string.Join ("", ((OrFilter)filter).Filters.Select (CompileFilter)) + ")";
			}
			else if (filter is NotFilter) {
				return "(!" + CompileFilter (((NotFilter)filter).InnerFilter) + ")";
			}
			else if (filter is EqualsFilter) {
				var f = (EqualsFilter)filter;
				var attrs = GetAttributeTypes (f.PropertyName);
				var q = attrs.Select (a => "(" + a + "=" + f.Value + ")");
				if (attrs.Length == 1) {
					return q.First ();
				}
				else {

					return "(|" + string.Join ("", q) + ")";
				}
			}
			else if (filter is ContainsFilter) {
				var f = (ContainsFilter)filter;
				var attrs = GetAttributeTypes (f.PropertyName);
				var q = attrs.Select (a => "(" + a + "=*" + f.Value + "*)");
				if (attrs.Length == 1) {
					return q.First ();
				}
				else {
					
					return "(|" + string.Join ("", q) + ")";
				}
			}
			else {
				throw new NotSupportedException (filter.GetType ().Name);
			}
		}

		static string[] GetAttributeTypes (string propertyName)
		{
			return ldapsFromProperty[propertyName]
				.Split (ldapSplits, StringSplitOptions.RemoveEmptyEntries);
		}

		static readonly Type ListType = typeof(List<string>); // For handling properties with multiple entries

		IList<Person> Search (string searchFilter, int sizeLimit)
		{
			var results = new List<Person> ();

			var conn = new LdapConnection ();
			conn.Connect (Host, Port);

			try {
				//
				// Query the server
				//
				var lsc = conn.Search (
					SearchBase,
					LdapConnection.SCOPE_SUB,
					searchFilter,
					null,
					false,
					new LdapSearchConstraints (0, 0, LdapSearchConstraints.DEREF_NEVER, sizeLimit, false, 1, null, 10));

				while (lsc.hasMore ()) {
					var nextEntry = lsc.next ();

					//
					// Create the person and load all their properties
					//
					// This code uses Reflection to create a mapping between LDAP
					// attribute types and .NET properties. See PropertyAttribute and
					// the static constructor of this class for details.
					//
					var person = new Person {
						Id = nextEntry.DN,
					};

					foreach (LdapAttribute attribute in nextEntry.getAttributeSet ()) {
						var attributeName = attribute.Name;
						var val = attribute.StringValue;

						PropertyInfo p;
						if (propertyFromLdap.TryGetValue (attributeName, out p)) {
							if (ListType.IsAssignableFrom (p.PropertyType)) {
								var list = (List<string>)p.GetValue (person, null);
								list.Add (val);
							}
							else {
								p.SetValue (person, val, null);
							}
						}
					}

					//
					// Make sure we only load people by looking for a last name
					//
					if (!string.IsNullOrEmpty (person.LastName)) {
						results.Add (person);
					}
				}
			}
			catch (LdapException e) {
				if (e.ResultCode == 4) {
					// More results pending...
				}
				else {
					throw;
				}
			}
			finally {
				conn.Disconnect ();
			}

			return results;
		}

		void ValidateConfiguration ()
		{
			if (string.IsNullOrWhiteSpace (Host)) {
				throw new InvalidOperationException ("Host must be set.");
			}
			if (Port == 0) {
				throw new InvalidOperationException ("Port must be set.");
			}
			if (string.IsNullOrWhiteSpace (SearchBase)) {
				throw new InvalidOperationException ("SearchBase must be set.");
			}
		}

		#region LDAP <-> Property Mapping

		static readonly Dictionary<string, PropertyInfo> propertyFromLdap;
		static readonly Dictionary<string, string> ldapsFromProperty;
		static readonly char[] ldapSplits = new [] { ',', ' ', ';' };

		static LdapDirectoryService ()
		{
			var q = from p in typeof(Person).GetProperties ()
					where p.CanWrite
					let a = p.GetCustomAttributes (typeof (PropertyAttribute), true).Cast<PropertyAttribute> ().FirstOrDefault ()
					where a != null
					select new { Property = p, Info = a };
			var props = q.ToArray ();

			propertyFromLdap = new Dictionary<string, PropertyInfo>();
			ldapsFromProperty = new Dictionary<string, string>();

			foreach (var p in props) {
				var ldaps = p.Info.Ldap.Split (ldapSplits, StringSplitOptions.RemoveEmptyEntries);
				foreach (var l in ldaps) {
					propertyFromLdap[l] = p.Property;
				}

				ldapsFromProperty[p.Property.Name] = p.Info.Ldap;
			}
		}

		#endregion
	}
}
