# 10: Configurações em camadas

**What to build:** Opções declaradas no código, resolução sistema > organização > unidade com trava, histórico e restauração, vigência a partir de instante. Opções iniciais: tempos de sessão, política de bloqueio, fuso e virada do dia operacional. Spec 5.7.
**Blocked by:** 02, 04
**Touches:** `backend/src/Organizacao/**`, `backend/migrations/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Audit
**Status:** pending

- [ ] Herança, trava, histórico e restauração testados
- [ ] Mudança com efeito operacional vale a partir do instante definido e não retroage
- [ ] Alteração exige `organizacao:configurar` e é auditada
- [ ] Tempos de sessão do ticket 04 passam a ler daqui
- [ ] Resolvedor do dia operacional continua com o padrão (persistência real é o 2.10b)
