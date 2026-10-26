// MQL4 fixture: Issue #143 — pure virtual function specifiers
// MQL4 build 600+ accepts the same two pure-specifier spellings as MQL5
// (docs.mql4.com compile error 381: only "=NULL" or "=0" are allowed).
class CAnimal
{
private:
    double m_legs_count;

public:
    virtual void Sound() = 0;
};

class CBird
{
public:
    virtual void Fly() = NULL;   // documented "= NULL" spelling
};

class CCat : public CAnimal
{
public:
    virtual void Sound() { }     // defined override — regression guard
};

class CDog : public CAnimal
{
public:
    virtual void Sound();        // declared, not pure — regression guard
};

void OnStart()
{
    CCat cat;
    CDog dog;
}
