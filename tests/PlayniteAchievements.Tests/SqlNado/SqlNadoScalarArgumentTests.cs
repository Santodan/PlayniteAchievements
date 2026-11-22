using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// <c>ExecuteScalar&lt;string&gt;(sql, text)</c> resolves to SQLNado's overload taking a
    /// default value, so the text is never bound; an argument array binds it.
    /// </summary>
    [TestClass]
    public class SqlNadoScalarArgumentTests
    {
        [TestMethod]
        public void AStringScalarArgument_BindsOnlyInAnArgumentArray()
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-scalar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var db = new SQLiteDatabase(
                    Path.Combine(dir, "scalar.db"),
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE | SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    db.ExecuteNonQuery("CREATE TABLE t (k TEXT, v TEXT);");
                    db.ExecuteNonQuery("INSERT INTO t (k, v) VALUES ('key', 'value');");
                    const string Sql = "SELECT v FROM t WHERE k = ? LIMIT 1;";

                    Assert.AreNotEqual("value", db.ExecuteScalar<string>(Sql, "key"), "the text is the default value, not the argument");
                    Assert.AreEqual("value", db.ExecuteScalar<string>(Sql, new object[] { "key" }));

                    db.ExecuteNonQuery("CREATE TABLE n (id INTEGER);");
                    db.ExecuteNonQuery("INSERT INTO n (id) VALUES (7);");
                    const string CountSql = "SELECT COUNT(1) FROM n WHERE id = ?;";
                    const long Id = 7;

                    Assert.AreEqual(0L, db.ExecuteScalar<long>(CountSql, Id), "a lone long is the default value, not the argument");
                    Assert.AreEqual(1L, db.ExecuteScalar<long>(CountSql, new object[] { Id }));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
