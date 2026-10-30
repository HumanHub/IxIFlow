namespace IxIFlow.Tests.Infrastructure;

internal sealed partial class DatabaseSchemaFingerprint
{
    // System-named PK/default/check constraints differ across physical databases. Normalize just those
    // names; retain their parent, definition, columns, order, flags and all explicitly assigned names.
    private const string SqlServerCatalog = """
                                            SELECT s.name,USER_NAME(s.principal_id) FROM sys.schemas s;
                                            SELECT s.name,o.type,
                                              CASE WHEN COALESCE(k.is_system_named,d.is_system_named,ch.is_system_named,f.is_system_named,0)=1 THEN '<generated>' ELSE o.name END,
                                              OBJECT_SCHEMA_NAME(o.parent_object_id),OBJECT_NAME(o.parent_object_id),USER_NAME(o.principal_id)
                                            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
                                            LEFT JOIN sys.key_constraints k ON k.object_id=o.object_id
                                            LEFT JOIN sys.default_constraints d ON d.object_id=o.object_id
                                            LEFT JOIN sys.check_constraints ch ON ch.object_id=o.object_id
                                            LEFT JOIN sys.foreign_keys f ON f.object_id=o.object_id WHERE o.is_ms_shipped=0;
                                            SELECT OBJECT_SCHEMA_NAME(c.object_id),OBJECT_NAME(c.object_id),c.column_id,c.name,
                                              SCHEMA_NAME((SELECT schema_id FROM sys.types WHERE user_type_id=c.user_type_id)),TYPE_NAME(c.user_type_id),c.max_length,c.precision,c.scale,c.collation_name,
                                              c.is_nullable,c.is_ansi_padded,c.is_rowguidcol,c.is_identity,c.is_computed,c.is_filestream,c.is_sparse,c.is_column_set,
                                              c.generated_always_type,cc.definition,cc.is_persisted,dc.definition,
                                              CONVERT(varchar(40),ic.seed_value),CONVERT(varchar(40),ic.increment_value),ic.is_not_for_replication
                                            FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
                                            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
                                            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
                                            LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
                                            WHERE o.is_ms_shipped=0;
                                            SELECT OBJECT_SCHEMA_NAME(i.object_id),OBJECT_NAME(i.object_id),
                                              CASE WHEN k.is_system_named=1 THEN '<generated>' ELSE i.name END,i.type,i.is_unique,i.is_primary_key,
                                              i.is_unique_constraint,i.is_disabled,i.has_filter,i.filter_definition,i.fill_factor,i.is_padded,
                                              i.ignore_dup_key,i.allow_row_locks,i.allow_page_locks,ds.name,
                                              ic.index_column_id,COL_NAME(ic.object_id,ic.column_id),ic.key_ordinal,ic.is_descending_key,ic.is_included_column
                                            FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id
                                            LEFT JOIN sys.key_constraints k ON k.parent_object_id=i.object_id AND k.unique_index_id=i.index_id
                                            LEFT JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                                            LEFT JOIN sys.data_spaces ds ON ds.data_space_id=i.data_space_id WHERE o.is_ms_shipped=0;
                                            SELECT OBJECT_SCHEMA_NAME(f.parent_object_id),OBJECT_NAME(f.parent_object_id),
                                              CASE WHEN f.is_system_named=1 THEN '<generated>' ELSE f.name END,
                                              OBJECT_SCHEMA_NAME(f.referenced_object_id),OBJECT_NAME(f.referenced_object_id),f.delete_referential_action,
                                              f.update_referential_action,f.is_disabled,f.is_not_trusted,f.is_not_for_replication,fc.constraint_column_id,
                                              COL_NAME(fc.parent_object_id,fc.parent_column_id),COL_NAME(fc.referenced_object_id,fc.referenced_column_id)
                                            FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id;
                                            SELECT OBJECT_SCHEMA_NAME(parent_object_id),OBJECT_NAME(parent_object_id),
                                              CASE WHEN is_system_named=1 THEN '<generated>' ELSE name END,definition,is_disabled,is_not_trusted,
                                              is_not_for_replication,parent_column_id FROM sys.check_constraints WHERE is_ms_shipped=0;
                                            SELECT OBJECT_SCHEMA_NAME(m.object_id),OBJECT_NAME(m.object_id),m.definition,m.uses_ansi_nulls,
                                              m.uses_quoted_identifier,m.is_schema_bound,m.execute_as_principal_id FROM sys.sql_modules m
                                              JOIN sys.objects o ON o.object_id=m.object_id WHERE o.is_ms_shipped=0;
                                            SELECT OBJECT_SCHEMA_NAME(object_id),name,is_disabled,is_instead_of_trigger,is_not_for_replication
                                              FROM sys.triggers WHERE is_ms_shipped=0;
                                            SELECT SCHEMA_NAME(schema_id),name,TYPE_NAME(user_type_id),CONVERT(varchar(40),start_value),
                                              CONVERT(varchar(40),increment),CONVERT(varchar(40),minimum_value),CONVERT(varchar(40),maximum_value),
                                              is_cycling,is_cached,cache_size FROM sys.sequences WHERE is_ms_shipped=0;
                                            SELECT SCHEMA_NAME(schema_id),name,TYPE_NAME(system_type_id),max_length,precision,scale,is_nullable,
                                              is_assembly_type,is_table_type FROM sys.types WHERE is_user_defined=1;
                                            SELECT name,type,authentication_type,default_schema_name FROM sys.database_principals WHERE principal_id>4;
                                            SELECT USER_NAME(role_principal_id),USER_NAME(member_principal_id) FROM sys.database_role_members;
                                            SELECT class,CASE class WHEN 0 THEN '' WHEN 1 THEN OBJECT_SCHEMA_NAME(major_id)+'.'+OBJECT_NAME(major_id)
                                              WHEN 3 THEN SCHEMA_NAME(major_id) ELSE CONVERT(nvarchar(20),major_id) END,minor_id,
                                              USER_NAME(grantee_principal_id),USER_NAME(grantor_principal_id),type,state FROM sys.database_permissions;
                                            SELECT OBJECT_SCHEMA_NAME(object_id),OBJECT_NAME(object_id),lock_escalation,uses_ansi_nulls,
                                              is_replicated,is_memory_optimized,temporal_type FROM sys.tables WHERE is_ms_shipped=0;
                                            SELECT OBJECT_SCHEMA_NAME(object_id),OBJECT_NAME(object_id),index_id,partition_number,data_compression
                                              FROM sys.partitions WHERE object_id IN (SELECT object_id FROM sys.tables WHERE is_ms_shipped=0);
                                            SELECT class,major_id,minor_id,name,CONVERT(nvarchar(max),value) FROM sys.extended_properties;
                                            SELECT OBJECT_SCHEMA_NAME(s.object_id),OBJECT_NAME(s.object_id),s.name,s.no_recompute,s.has_filter,
                                              s.filter_definition,sc.stats_column_id,COL_NAME(sc.object_id,sc.column_id)
                                              FROM sys.stats s JOIN sys.stats_columns sc ON sc.object_id=s.object_id AND sc.stats_id=s.stats_id
                                              WHERE s.user_created=1;
                                            """;

    // PostgreSQL appends each trigger's OID to RI_ConstraintTrigger_{a,c}. pg_identify_object
    // exposes that name even though the trigger is an implementation detail of a logical FK.
    // Normalize only genuine internal FK triggers with their exact generated name, on either end
    // of a dependency. User triggers (including lookalike names) and renamed internal triggers
    // keep their identities. Keep FK, table, action/check role and event distinct.
    private const string PostgreSqlForeignKeyTriggers = """
                                                        SELECT t.oid, json_build_array(fn.nspname,fc.relname,f.conname,n.nspname,c.relname,
                                                          left(t.tgname,length(t.tgname)-length(t.oid::text)),t.tgtype)::text AS identity
                                                        FROM pg_trigger t JOIN pg_constraint f ON f.oid=t.tgconstraint
                                                        JOIN pg_class fc ON fc.oid=f.conrelid JOIN pg_namespace fn ON fn.oid=fc.relnamespace
                                                        JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                                                        WHERE t.tgisinternal AND f.contype='f'
                                                          AND t.tgname IN ('RI_ConstraintTrigger_a_'||t.oid::text,'RI_ConstraintTrigger_c_'||t.oid::text)
                                                          AND n.nspname !~ '^pg_' AND n.nspname<>'information_schema'
                                                        """;

    private static readonly string PostgreSqlCatalog = $$"""
                                                         SELECT nspname,pg_get_userbyid(nspowner),nspacl::text FROM pg_namespace
                                                           WHERE nspname !~ '^pg_' AND nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,c.relkind,pg_get_userbyid(c.relowner),c.relacl::text,c.relpersistence,
                                                           c.reloptions::text,c.relrowsecurity,c.relforcerowsecurity,c.relreplident,c.relispartition,am.amname
                                                           FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_am am ON am.oid=c.relam
                                                           WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,a.attnum,a.attname,format_type(a.atttypid,a.atttypmod),
                                                           a.attnotnull,a.attidentity,a.attgenerated,a.attisdropped,a.attstorage,a.attcompression,a.attstattarget,
                                                           co.collname,co.collprovider,co.collcollate,co.collctype,a.attacl::text,a.attoptions::text,
                                                           pg_get_expr(d.adbin,d.adrelid)
                                                           FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                                                           LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                                                           LEFT JOIN pg_collation co ON co.oid=a.attcollation
                                                           WHERE a.attnum>0 AND n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,con.conname,con.contype,con.condeferrable,con.condeferred,con.convalidated,
                                                           pg_get_constraintdef(con.oid,true) FROM pg_constraint con JOIN pg_namespace n ON n.oid=con.connamespace
                                                           LEFT JOIN pg_class c ON c.oid=con.conrelid WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,ic.relname,pg_get_indexdef(i.indexrelid),i.indisvalid,i.indisready,i.indislive,
                                                           i.indisreplident,i.indisclustered FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid
                                                           JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_class ic ON ic.oid=i.indexrelid
                                                           WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,t.tgname,pg_get_triggerdef(t.oid,true),t.tgenabled
                                                           FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                                                           WHERE NOT t.tgisinternal AND n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         WITH fk_triggers AS ({{PostgreSqlForeignKeyTriggers}})
                                                         SELECT f.identity,t.tgenabled,t.tgfoid::regprocedure::text,t.tgdeferrable,t.tginitdeferred,
                                                           t.tgconstrrelid::regclass::text,t.tgconstrindid::regclass::text,t.tgattr::text,t.tgnargs,t.tgargs,
                                                           pg_get_expr(t.tgqual,t.tgrelid),t.tgoldtable,t.tgnewtable,parent.identity
                                                           FROM fk_triggers f JOIN pg_trigger t ON t.oid=f.oid
                                                           LEFT JOIN fk_triggers parent ON parent.oid=t.tgparentid;
                                                         SELECT n.nspname,p.proname,pg_get_function_identity_arguments(p.oid),p.prokind,
                                                           CASE WHEN p.prokind<>'a' THEN pg_get_functiondef(p.oid) END,p.proacl::text
                                                           FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                                                           WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,s.seqstart,s.seqincrement,s.seqmin,s.seqmax,s.seqcache,s.seqcycle
                                                           FROM pg_sequence s JOIN pg_class c ON c.oid=s.seqrelid JOIN pg_namespace n ON n.oid=c.relnamespace;
                                                         SELECT n.nspname,t.typname,t.typtype,t.typcategory,t.typnotnull,t.typdefault,t.typacl::text,
                                                           pg_get_userbyid(t.typowner),format_type(NULLIF(t.typbasetype,0),t.typtypmod)
                                                           FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
                                                           WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,r.rulename,pg_get_ruledef(r.oid,true) FROM pg_rewrite r
                                                           JOIN pg_class c ON c.oid=r.ev_class JOIN pg_namespace n ON n.oid=c.relnamespace
                                                           WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema';
                                                         SELECT n.nspname,c.relname,p.polname,p.polcmd,p.polpermissive,p.polroles::text,
                                                           pg_get_expr(p.polqual,p.polrelid),pg_get_expr(p.polwithcheck,p.polrelid) FROM pg_policy p
                                                           JOIN pg_class c ON c.oid=p.polrelid JOIN pg_namespace n ON n.oid=c.relnamespace;
                                                         SELECT e.extname,e.extversion,n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace;
                                                         SELECT n.nspname,d.defaclobjtype,pg_get_userbyid(d.defaclrole),d.defaclacl::text
                                                           FROM pg_default_acl d LEFT JOIN pg_namespace n ON n.oid=d.defaclnamespace;
                                                         SELECT pubname,pg_get_userbyid(pubowner),puballtables,pubinsert,pubupdate,pubdelete,pubtruncate,pubviaroot FROM pg_publication;
                                                         SELECT evtname,evtevent,pg_get_userbyid(evtowner),evtenabled,evttags::text,evtfoid::regprocedure::text FROM pg_event_trigger;
                                                         SELECT lanname,pg_get_userbyid(lanowner),lanispl,lanpltrusted,lanacl::text FROM pg_language;
                                                         SELECT n.nspname,t.typname,e.enumsortorder,e.enumlabel FROM pg_enum e JOIN pg_type t ON t.oid=e.enumtypid
                                                           JOIN pg_namespace n ON n.oid=t.typnamespace;
                                                         WITH fk_triggers AS ({{PostgreSqlForeignKeyTriggers}})
                                                         SELECT obj.type,obj.schema,coalesce(ot.identity,obj.identity),d.deptype,
                                                           ref.type,ref.schema,coalesce(rt.identity,ref.identity)
                                                           FROM pg_depend d CROSS JOIN LATERAL pg_identify_object(d.classid,d.objid,d.objsubid) obj
                                                           CROSS JOIN LATERAL pg_identify_object(d.refclassid,d.refobjid,d.refobjsubid) ref
                                                           LEFT JOIN fk_triggers ot ON d.classid='pg_trigger'::regclass AND ot.oid=d.objid
                                                           LEFT JOIN fk_triggers rt ON d.refclassid='pg_trigger'::regclass AND rt.oid=d.refobjid
                                                           WHERE (obj.schema !~ '^pg_' AND obj.schema<>'information_schema')
                                                              OR (obj.schema IS NULL AND d.objid>=16384);
                                                         SELECT obj.type,obj.schema,obj.identity,description FROM pg_description d
                                                           CROSS JOIN LATERAL pg_identify_object(d.classoid,d.objoid,d.objsubid) obj
                                                           WHERE obj.schema !~ '^pg_' AND obj.schema<>'information_schema';
                                                         """;
}