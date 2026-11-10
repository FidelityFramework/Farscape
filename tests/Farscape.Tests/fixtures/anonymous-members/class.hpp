// A C++ record with an anonymous union member (#177): represented on ClassDecl with its clang-measured layout.
struct fx_cpp_am
{
  int tag;
  union { int i; float f; };
};

void fx_cpp_am_read(fx_cpp_am *am);
