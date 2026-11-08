/* Callback and projection refusals (#195) and positional parameter names (#194), in the shapes
   sqlite3.h declares them: an optional row callback whose entry takes a void pointer (FS9144,
   sqlite3_exec), a destructor callback with no nonnull evidence (FS9146, sqlite3_bind_blob), and
   prototypes that leave parameters unnamed (sqlite3_bind_int). */
typedef struct fx_db fx_db;
typedef int (*fx_row)(void *, int, char **, char **);

int fx_exec(fx_db *db, const char *sql, fx_row callback, void *arg, char **errmsg);

int fx_bind_blob(fx_db *, int, const void *, int, void (*)(void *));

int fx_bind_int(fx_db *, int, int value, int);
