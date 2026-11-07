/* Issue #54 fixture: the connect declaration's unit; it declares no signal payload type. */
typedef struct FxProbe FxProbe;
typedef struct GClosure GClosure;
typedef void *gpointer;
typedef char gchar;
typedef unsigned long gulong;
typedef void (*GCallback)(void);
typedef void (*GClosureNotify)(gpointer, GClosure *);
typedef enum { G_CONNECT_DEFAULT = 0 } GConnectFlags;
gulong g_signal_connect_data(gpointer instance, const gchar *detailed_signal, GCallback c_handler,
    gpointer data, GClosureNotify destroy_data, GConnectFlags connect_flags);
