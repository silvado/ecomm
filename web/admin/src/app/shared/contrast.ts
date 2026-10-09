/** Mesmas regras do back-end (HexColor/StoreBranding): contraste WCAG 2.x, só para aviso. */
export const MIN_TEXT_CONTRAST = 4.5;
export const MIN_COMPONENT_CONTRAST = 3;

const HEX = /^#[0-9a-fA-F]{6}$/;

export function isHexColor(value: string): boolean {
  return HEX.test(value.trim());
}

function luminance(hex: string): number {
  const channel = (offset: number) => {
    const c = parseInt(hex.slice(offset, offset + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
}

/** Razão de contraste entre 1 e 21; NaN se alguma cor for inválida. */
export function contrastRatio(a: string, b: string): number {
  if (!isHexColor(a) || !isHexColor(b)) return NaN;
  const [x, y] = [luminance(a.trim()), luminance(b.trim())];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

/** Branco ou preto, o que for mais legível sobre a cor (texto dos botões). */
export function readableOn(color: string): string {
  return contrastRatio(color, '#FFFFFF') >= contrastRatio(color, '#000000') ? '#FFFFFF' : '#000000';
}
