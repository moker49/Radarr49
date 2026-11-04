import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputHelpText from 'Components/Form/FormInputHelpText';
import FormLabel from 'Components/Form/FormLabel';
import { sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import QualityProfileItemDragPreview from './QualityProfileItemDragPreview';
import QualityProfileItemDragSource from './QualityProfileItemDragSource';
import styles from './QualityProfileItems.css';

class CompositeProfileItems extends Component {
  state = { dragQualityIndex: null, dropQualityIndex: null, dropPosition: null };

  onDragMove = (position) => {
    this.setState(position);
  };

  onDragEnd = (didDrop) => {
    const { dragQualityIndex, dropQualityIndex, dropPosition } = this.state;
    if (didDrop && dragQualityIndex !== null && dropQualityIndex !== null) {
      const rows = [...this.props.items];
      const source = Number(dragQualityIndex) - 1;
      const target = Number(dropQualityIndex) - 1;
      let destination = target + (dropPosition === 'below' ? 1 : 0);
      if (source < destination) {
        destination--;
      }
      const [row] = rows.splice(source, 1);
      rows.splice(destination, 0, row);
      this.props.onOrderChange(rows);
    }
    this.setState({ dragQualityIndex: null, dropQualityIndex: null, dropPosition: null });
  };

  render() {
    const { items, onAllowedChange } = this.props;
    const { dropQualityIndex, dropPosition } = this.state;
    return (
      <FormGroup size={sizes.EXTRA_SMALL}>
        <FormLabel size={sizes.SMALL}>{translate('Profiles')}</FormLabel>
        <div>
          <FormInputHelpText text={translate('CompositeProfileItemsHelp')} />
          <div className={styles.qualities}>
            {items.map((item, index) => (
              <QualityProfileItemDragSource
                key={item.id}
                editGroups={false}
                qualityId={item.id}
                qualityIndex={`${index + 1}`}
                name={item.name}
                allowed={item.allowed}
                isDraggingUp={dropQualityIndex !== null && dropPosition === 'above'}
                isDraggingDown={dropQualityIndex !== null && dropPosition === 'below'}
                onQualityProfileItemAllowedChange={onAllowedChange}
                onQualityProfileItemDragMove={this.onDragMove}
                onQualityProfileItemDragEnd={this.onDragEnd}
              />
            ))}
            <QualityProfileItemDragPreview />
          </div>
        </div>
      </FormGroup>
    );
  }
}

CompositeProfileItems.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onOrderChange: PropTypes.func.isRequired,
  onAllowedChange: PropTypes.func.isRequired
};

export default CompositeProfileItems;
