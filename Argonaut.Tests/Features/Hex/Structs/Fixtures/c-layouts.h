/* Struct layouts recorded from Clang by record-c-layouts.sh, for StructLayoutCTests: every
   struct here is laid out by the struct decoder for both targets and compared with Clang's
   sizes, alignments and field offsets. No #include - the recording script declares the
   <stdint.h> types for Clang, and the decoder knows them already. */

#define NAME_LENGTH 16
#define DOUBLE_NAME (NAME_LENGTH * 2)
static const int kSlots = 3;
constexpr int kGrid = 4;

typedef unsigned int DWORD;
typedef unsigned short WORD;
typedef unsigned char BYTE;

struct Basic { char c; int i; short s; double d; };
struct Tail { double d; char c; };
struct Chars { char a; signed char b; unsigned char c; char16_t w16; char32_t w32; wchar_t w; };
struct Longs { char c; long l; unsigned long ul; long long ll; unsigned long long ull; long int li; unsigned long int uli; };
struct Pointers { char c; void* p; const char* name; int** pp; void (*callback)(int, char*); };
struct Sizes { char c; size_t size; intptr_t ip; uintptr_t up; ptrdiff_t diff; };
struct FixedWidth { uint8_t a; int16_t b; uint32_t c; int64_t d; bool e; float f; };
struct Arrays { char name[NAME_LENGTH]; int grid[kGrid][kSlots]; short pair[2]; char wide[DOUBLE_NAME + 1]; };
struct Nested { char tag; struct Basic basic; Tail tail[2]; };
typedef struct { BYTE kind; DWORD value; WORD flags; } Header;
typedef struct Tagged { int id; Header header; } Tagged, *PTagged;
struct Holder { Header first; PTagged next; Tagged inline_one; };
struct Outer { struct Inner { short x; double y; } inner; int count; Inner more[2]; };
struct Methods
{
    int a;
    int get() const { return a; }
    Methods() : a(0) {}
    static int s;
    char b;
    void set(int v);
};
class Access { public: int a; private: char b; protected: double c; };
struct Empty {};
struct HasEmpty { char c; Empty e; int i; };
struct Sample { int32_t count; float ratio; bool enabled; char name[256]; };

/* Packing and alignment. */
#pragma pack(push, 1)
struct Packed1 { char c; int i; short s; double d; };
#pragma pack(push, 2)
struct Packed2 { char c; int i; char d; double e; };
#pragma pack(pop)
struct BackToPacked1 { char c; long long l; };
#pragma pack(pop)
struct Unpacked { char c; long long l; };
#pragma pack(4)
struct Pack4 { char c; double d; };
#pragma pack()
struct __attribute__((packed)) AttrPacked { char c; int i; short s; };
struct alignas(16) Aligned16 { char c; };
struct FieldAligned { char c; alignas(8) int i; char d; };
struct HoldsAligned { char c; Aligned16 a; };

/* Unions and anonymous members. */
union Number { int i; float f; double d; char bytes[3]; };
struct WithUnion { char tag; union { int i; double d; } value; char after; };
struct AnonymousMembers { char tag; union { int i; float f; }; struct { short x, y; }; int after; };

/* Enums, as fields and as lengths. */
enum Color { Red, Green = 5, Blue };
enum class Small : unsigned char { A, B };
enum Big : long long { Huge = 1LL << 40 };
typedef enum { ModeA = 1, ModeB = 2 } Mode;
struct Enums { char c; Color color; Small small; Mode mode; char after; Big big; int counts[Blue]; };

/* Bitfields: the cases GCC/Clang and MSVC lay out differently. */
struct Bits { unsigned a : 3; unsigned b : 5; unsigned c : 30; int d; };
struct MixedBits { char a : 3; int b : 4; short c : 9; char d; };
struct ZeroBits { char a : 3; int : 0; char b : 2; };
struct UnnamedBits { char a; int : 5; char b; };
struct StraddleBits { unsigned short a : 12; unsigned short b : 6; unsigned short c : 6; };
struct BoolBits { bool a : 1; bool b : 1; unsigned char c : 4; long long d : 40; };
#pragma pack(push, 1)
struct PackedBits { char a : 3; int b : 7; short c : 12; };
#pragma pack(pop)

/* Single inheritance. */
struct BaseRecord { int id; char kind; };
struct DerivedRecord : BaseRecord { char extra; };
struct EmptyBase {};
struct DerivedFromEmpty : public EmptyBase { int x; };
struct Deeper : DerivedRecord { short s; };
