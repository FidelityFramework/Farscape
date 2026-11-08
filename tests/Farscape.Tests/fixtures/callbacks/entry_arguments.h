/* Callback entry arguments beyond ordered integers (#189, #190, #191): an opaque context the
   caller passes through, opaque record handles, counted pointer arrays, and registrations the
   library keeps until a release. */
typedef struct fx_db fx_db;
typedef struct fx_ctx fx_ctx;
typedef struct fx_value fx_value;

/* sqlite3_exec's shape: a row callback with the caller's context and two counted text arrays. */
int fx_exec(fx_db *db, const char *sql, int (*callback)(void *, int, char **, char **), void *context, char **errmsg);

/* An entry argument with no admitted representation. */
int fx_measure(int (*callback)(double), void *context);

/* xFunc's shape: an opaque context handle and a counted array of opaque value handles. */
int fx_register(fx_db *db, const char *name, int arity, void *app,
                void (*fn)(fx_ctx *, int, fx_value **), void (*destroy)(void *));
int fx_close(fx_db *db);
int fx_reset(int code);

/* sqlite3_busy_handler's shape: kept until replaced or the connection closes. */
int fx_set_handler(fx_db *db, int (*handler)(void *, int), void *arg);
