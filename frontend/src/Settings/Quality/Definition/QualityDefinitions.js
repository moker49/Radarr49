import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import PageSectionContent from 'Components/Page/PageSectionContent';
import translate from 'Utilities/String/translate';
import QualityDefinitionConnector from './QualityDefinitionConnector';
import styles from './QualityDefinitions.css';

class QualityDefinitions extends Component {

  //
  // Render

  render() {
    const {
      items,
      advancedSettings,
      showTitles = true,
      definitionComponent: DefinitionComponent = QualityDefinitionConnector,
      ...otherProps
    } = this.props;

    return (
      <FieldSet legend={translate('QualityDefinitions')}>
        <PageSectionContent
          errorMessage={translate('QualityDefinitionsLoadError')}
          {...otherProps}
        >
          <div className={styles.header}>
            <div className={styles.quality}>
              {translate('Quality')}
            </div>
            <div className={styles.title}>
              {showTitles ? translate('Title') : null}
            </div>
            <div className={styles.sizeLimit}>
              {translate('SizeLimit')}
            </div>

            {
              advancedSettings ?
                <div className={styles.megabytesPerMinute}>
                  {translate('MegabytesPerMinute')}
                </div> :
                null
            }
          </div>

          <div className={styles.definitions}>
            {
              items.map((item) => {
                return (
                  <DefinitionComponent
                    key={item.id}
                    {...item}
                    advancedSettings={advancedSettings}
                    showTitle={showTitles}
                  />
                );
              })
            }
          </div>

          <div className={styles.sizeLimitHelpTextContainer}>
            <div className={styles.sizeLimitHelpText}>
              {translate('QualityLimitsMovieRuntimeHelpText')}
            </div>
          </div>
        </PageSectionContent>
      </FieldSet>
    );
  }
}

QualityDefinitions.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  defaultProfile: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  advancedSettings: PropTypes.bool.isRequired,
  showTitles: PropTypes.bool,
  definitionComponent: PropTypes.elementType
};

export default QualityDefinitions;
