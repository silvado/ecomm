import { contrastRatio, isHexColor, readableOn } from './contrast';

describe('contraste (mesmas regras do back-end)', () => {
  it('segue o WCAG', () => {
    expect(contrastRatio('#000000', '#FFFFFF')).toBeCloseTo(21, 2);
    expect(contrastRatio('#777777', '#FFFFFF')).toBeCloseTo(4.48, 2);
    expect(contrastRatio('#767676', '#ffffff')).toBeCloseTo(4.54, 2);
  });

  it('cor inválida não gera número', () => {
    expect(isHexColor('vermelho')).toBe(false);
    expect(contrastRatio('#12345', '#FFFFFF')).toBeNaN();
  });

  it('texto do botão é o mais legível', () => {
    expect(readableOn('#FFEB3B')).toBe('#000000');
    expect(readableOn('#1F5FBF')).toBe('#FFFFFF');
  });
});
