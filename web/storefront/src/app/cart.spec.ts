import { TestBed } from '@angular/core/testing';
import { CartLine, CartStore, MAX_LINES } from './cart';

const line = (partId: string, quantity: number, problem: CartLine['problem'] = null): CartLine => ({
  partId, title: 'Peça', condition: 'used', unitPrice: 10, quantity, available: quantity, coverPhotoId: null, problem,
});

describe('CartStore', () => {
  beforeEach(() => localStorage.clear());

  function store(): CartStore {
    const cart = TestBed.inject(CartStore);
    TestBed.tick(); // afterNextRender: lê o localStorage depois da "hidratação"
    return cart;
  }

  it('soma quantidades da mesma peça, altera, remove e persiste', () => {
    const cart = store();

    cart.add('a');
    cart.add('a', 2);
    cart.add('b');
    cart.set('b', 4);
    expect(cart.list()).toEqual([{ partId: 'a', quantity: 3 }, { partId: 'b', quantity: 4 }]);
    expect(cart.count()).toBe(7);

    cart.set('a', 0);
    cart.remove('b');
    expect(cart.list()).toEqual([]);
    expect(JSON.parse(localStorage.getItem('carrinho')!)).toEqual([]);
  });

  it('retoma o carrinho salvo e ignora dados corrompidos', () => {
    localStorage.setItem('carrinho', JSON.stringify([{ partId: 'a', quantity: 2 }, { partId: 3, quantity: 1 }, { partId: 'b', quantity: -1 }]));
    expect(store().list()).toEqual([{ partId: 'a', quantity: 2 }]);
  });

  it('carrinho ilegível começa vazio', () => {
    localStorage.setItem('carrinho', '{não é json');
    expect(store().list()).toEqual([]);
  });

  it('aplica o que o servidor decidiu: indisponível sai, quantidade vai ao estoque', () => {
    const cart = store();
    cart.add('a', 5);
    cart.add('b', 1);
    cart.add('c', 1);

    cart.reconcile([line('a', 2, 'quantityReduced'), line('b', 0, 'unavailable'), line('c', 1)]);

    expect(cart.list()).toEqual([{ partId: 'a', quantity: 2 }, { partId: 'c', quantity: 1 }]);
  });

  it(`no máximo ${MAX_LINES} peças diferentes`, () => {
    const cart = store();
    for (let i = 0; i <= MAX_LINES; i++) cart.add(`p${i}`);

    expect(cart.list().length).toBe(MAX_LINES);
  });
});
